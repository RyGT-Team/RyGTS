// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RyGT
{
    sealed class VulkanDevice
    {
        public int Index;
        public string Name, Uuid;
        public uint ApiVersion, VendorId, DeviceType;
        public ulong DeviceLocalBytes;
        public override string ToString() { return Name; }
    }

    static class ShaderCompilationPolicy
    {
        // Match the renderer's automatic device ranking, including the CPU exclusion.
        static int Compare(VulkanDevice left, VulkanDevice right)
        {
            const uint api = (1u << 22) | (3u << 12);
            int order = (left.ApiVersion >= api).CompareTo(right.ApiVersion >= api);
            if (order == 0) order = (left.DeviceType == 2).CompareTo(right.DeviceType == 2);
            if (order == 0) order = (left.DeviceType != 4).CompareTo(right.DeviceType != 4);
            return order != 0 ? order : left.DeviceLocalBytes.CompareTo(right.DeviceLocalBytes);
        }

        public static bool AllowsKeepGoing(IList<VulkanDevice> devices, string uuid, int index)
        {
            if (devices.Count == 0) throw new InvalidOperationException("No Vulkan device is available.");
            if (!string.IsNullOrEmpty(uuid) || index >= 0)
            {
                foreach (var device in devices)
                    if (!string.IsNullOrEmpty(uuid)
                        ? string.Equals(uuid, device.Uuid, StringComparison.OrdinalIgnoreCase)
                        : device.Index == index)
                        return device.VendorId != 0x1002;
                throw new InvalidOperationException("The selected Vulkan device is unavailable.");
            }
            var best = devices[0];
            foreach (var device in devices) if (Compare(device, best) > 0) best = device;
            // std::sort does not specify tie order. Automatic must be safe for any winner.
            foreach (var device in devices)
                if (Compare(device, best) == 0 && device.VendorId == 0x1002) return false;
            return true;
        }
    }

    // Enumerate Vulkan itself, not DXGI: its indices are the ones the renderer consumes.
    static class VulkanDevices
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ApplicationInfo
        {
            public uint Type;
            public IntPtr Next, Name;
            public uint Version;
            public IntPtr Engine;
            public uint EngineVersion, ApiVersion;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct InstanceInfo
        {
            public uint Type;
            public IntPtr Next;
            public uint Flags;
            public IntPtr Application;
            public uint LayerCount;
            public IntPtr Layers;
            public uint ExtensionCount;
            public IntPtr Extensions;
        }
        [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
        static extern int vkCreateInstance(ref InstanceInfo info, IntPtr allocator, out IntPtr instance);
        [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
        static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
        [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
        static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, string name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate int EnumerateDevices(IntPtr instance, ref uint count, IntPtr devices);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void GetProperties(IntPtr physical, IntPtr properties);

        static T Function<T>(IntPtr instance, string name) where T : class
        {
            IntPtr address = vkGetInstanceProcAddr(instance, name);
            if (address == IntPtr.Zero) throw new Exception("Vulkan cannot query devices (" + name + ").");
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        public static List<VulkanDevice> Enumerate()
        {
            IntPtr instance = IntPtr.Zero, application = IntPtr.Zero, devices = IntPtr.Zero;
            IntPtr properties = IntPtr.Zero, identity = IntPtr.Zero, memory = IntPtr.Zero;
            try
            {
                var app = new ApplicationInfo { Type = 0, ApiVersion = (1u << 22) | (3u << 12) };
                application = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ApplicationInfo)));
                Marshal.StructureToPtr(app, application, false);
                var info = new InstanceInfo { Type = 1, Application = application };
                int result = vkCreateInstance(ref info, IntPtr.Zero, out instance);
                if (result != 0) throw new Exception("Vulkan device query failed (" + result + ").");
                var enumerate = Function<EnumerateDevices>(instance, "vkEnumeratePhysicalDevices");
                var get = Function<GetProperties>(instance, "vkGetPhysicalDeviceProperties2");
                var getMemory = Function<GetProperties>(instance, "vkGetPhysicalDeviceMemoryProperties");
                uint count = 0;
                var list = new List<VulkanDevice>();
                // VK_INCOMPLETE can occur if an adapter appears between the two calls.
                for (int attempt = 0; attempt < 4; ++attempt)
                {
                    result = enumerate(instance, ref count, IntPtr.Zero);
                    if (result != 0 || count > 256) throw new Exception("Vulkan device enumeration failed (" + result + ").");
                    if (count == 0) return list;
                    if (devices != IntPtr.Zero) Marshal.FreeHGlobal(devices);
                    devices = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
                    result = enumerate(instance, ref count, devices);
                    if (result == 5) continue;
                    if (result != 0) throw new Exception("Vulkan device enumeration failed (" + result + ").");
                    break;
                }
                if (result != 0) throw new Exception("Vulkan devices kept changing. Try again.");
                int header = IntPtr.Size == 8 ? 16 : 8;
                properties = Marshal.AllocHGlobal(4096);
                identity = Marshal.AllocHGlobal(64);
                memory = Marshal.AllocHGlobal(4096);
                for (int i = 0; i < count; ++i)
                {
                    Marshal.Copy(new byte[4096], 0, properties, 4096);
                    Marshal.Copy(new byte[64], 0, identity, 64);
                    Marshal.WriteInt32(properties, 1000059001); // PHYSICAL_DEVICE_PROPERTIES_2
                    Marshal.WriteIntPtr(properties, IntPtr.Size, identity);
                    Marshal.WriteInt32(identity, 1000071004); // PHYSICAL_DEVICE_ID_PROPERTIES
                    get(Marshal.ReadIntPtr(devices, i * IntPtr.Size), properties);
                    Marshal.Copy(new byte[4096], 0, memory, 4096);
                    getMemory(Marshal.ReadIntPtr(devices, i * IntPtr.Size), memory);
                    int heapCount = Marshal.ReadInt32(memory, 260);
                    if (heapCount < 0 || heapCount > 16) throw new Exception("Invalid Vulkan memory heap count.");
                    ulong localBytes = 0;
                    for (int heap = 0; heap < heapCount; ++heap)
                        if ((Marshal.ReadInt32(memory, 264 + heap * 16 + 8) & 1) != 0)
                            localBytes = Math.Max(localBytes, unchecked((ulong)Marshal.ReadInt64(memory, 264 + heap * 16)));
                    byte[] name = new byte[256], uuid = new byte[16];
                    Marshal.Copy(IntPtr.Add(properties, header + 20), name, 0, name.Length);
                    Marshal.Copy(IntPtr.Add(identity, header), uuid, 0, uuid.Length);
                    int end = Array.IndexOf(name, (byte)0);
                    list.Add(new VulkanDevice {
                        Index = i, Name = Encoding.UTF8.GetString(name, 0, end < 0 ? name.Length : end),
                        Uuid = BitConverter.ToString(uuid).Replace("-", "").ToLowerInvariant(),
                        ApiVersion = unchecked((uint)Marshal.ReadInt32(properties, header)),
                        VendorId = unchecked((uint)Marshal.ReadInt32(properties, header + 8)),
                        DeviceType = unchecked((uint)Marshal.ReadInt32(properties, header + 16)),
                        DeviceLocalBytes = localBytes
                    });
                }
                return list;
            }
            finally
            {
                if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
                if (identity != IntPtr.Zero) Marshal.FreeHGlobal(identity);
                if (properties != IntPtr.Zero) Marshal.FreeHGlobal(properties);
                if (devices != IntPtr.Zero) Marshal.FreeHGlobal(devices);
                if (instance != IntPtr.Zero) vkDestroyInstance(instance, IntPtr.Zero);
                if (application != IntPtr.Zero) Marshal.FreeHGlobal(application);
            }
        }
    }
}
