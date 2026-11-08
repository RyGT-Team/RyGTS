// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RyGT
{
    static class ShaderCompilationPolicyTests
    {
        const uint Api13 = (1u << 22) | (3u << 12);
        const uint Amd = 0x1002, Nvidia = 0x10de, Intel = 0x8086;
        static int checks;

        static void Check(bool condition, string name)
        {
            ++checks;
            if (!condition) throw new Exception("FAILED: " + name);
        }

        static VulkanDevice Gpu(int index, string uuid, uint vendor, uint type, ulong memory, uint api)
        {
            return new VulkanDevice { Index = index, Uuid = uuid, VendorId = vendor,
                DeviceType = type, DeviceLocalBytes = memory, ApiVersion = api, Name = uuid };
        }

        static bool Allowed(string uuid, int index, params VulkanDevice[] devices)
        {
            return ShaderCompilationPolicy.AllowsKeepGoing(devices, uuid, index);
        }

        static object Call(LoaderForm form, string name, params object[] args)
        {
            MethodInfo method = typeof(LoaderForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new Exception("Missing launcher method: " + name);
            return method.Invoke(form, args);
        }

        static object Field(LoaderForm form, string name)
        {
            FieldInfo field = typeof(LoaderForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new Exception("Missing launcher field: " + name);
            return field.GetValue(form);
        }

        static void Set(LoaderForm form, string section, string key, object value)
        {
            Call(form, "WriteSetting", section, key, value);
        }

        static int Mode(LoaderForm form)
        {
            return (int)Call(form, "ReadIntSetting", "GPU", "compile_mode", -1);
        }

        static void Apply(LoaderForm form, bool? allowed)
        {
            Call(form, "ApplyShaderCompilationPolicy", allowed);
        }

        static ComboBox FindDeviceCombo(Control parent)
        {
            var combo = parent as ComboBox;
            if (combo != null)
                foreach (object item in combo.Items)
                    if (item is VulkanDevice) return combo;
            foreach (Control child in parent.Controls)
            {
                var found = FindDeviceCombo(child);
                if (found != null) return found;
            }
            return null;
        }

        static void TestLiveDeviceSwitch(LoaderForm form, ComboBox mode)
        {
            var devices = FindDeviceCombo(form);
            Check(devices != null, "Vulkan device selector exists");
            VulkanDevice amd = null, other = null;
            foreach (object item in devices.Items)
            {
                var device = item as VulkanDevice;
                if (device == null || device.Index < 0) continue;
                if (device.VendorId == Amd) amd = device;
                else other = device;
            }
            if (amd == null || other == null)
            {
                Console.WriteLine("SKIP: live GPU switching requires both AMD and non-AMD Vulkan devices");
                return;
            }
            devices.SelectedItem = other;
            Check(mode.Items.Count == 3, "selecting non-AMD restores Keep going");
            mode.SelectedIndex = 2;
            Check(Mode(form) == 2, "selecting Keep going persists on non-AMD");
            devices.SelectedItem = amd;
            Check(mode.Items.Count == 2 && mode.SelectedIndex == 1 && Mode(form) == 1,
                  "switching to actual AMD device removes and replaces Keep going");
            devices.SelectedItem = other;
            Check(mode.Items.Count == 3 && mode.SelectedIndex == 1 && Mode(form) == 1,
                  "switching back preserves fallback while restoring option");
        }

        static void TestRanking()
        {
            var amdIntegrated = Gpu(0, "amd-igpu", Amd, 1, 4, Api13);
            var nvidiaDiscrete = Gpu(1, "nvidia", Nvidia, 2, 8, Api13);
            var amdDiscrete = Gpu(2, "amd-dgpu", Amd, 2, 16, Api13);
            var intelIntegrated = Gpu(3, "intel", Intel, 1, 8, Api13);
            Check(Allowed("", -1, amdIntegrated, nvidiaDiscrete), "automatic NVIDIA discrete beats AMD integrated");
            Check(!Allowed("", -1, amdDiscrete, intelIntegrated), "automatic AMD discrete beats Intel integrated");
            Check(!Allowed("AMD-IGPU", 1, amdIntegrated, nvidiaDiscrete), "UUID selects AMD despite stale NVIDIA index");
            Check(Allowed("nvidia", 2, nvidiaDiscrete, amdDiscrete), "UUID selects NVIDIA despite AMD index");
            Check(!Allowed("", 2, nvidiaDiscrete, amdDiscrete), "explicit AMD index");
            Check(Allowed("", 1, nvidiaDiscrete, amdDiscrete), "explicit NVIDIA index");
            Check(Allowed("", -1, Gpu(0, "old-amd", Amd, 2, 32, Api13 - 1), intelIntegrated), "Vulkan 1.3 outranks older discrete GPU");
            Check(Allowed("", -1, Gpu(0, "cpu", Amd, 4, 128, Api13), intelIntegrated), "non-CPU outranks software memory");
            Check(Allowed("", -1, Gpu(0, "amd-small", Amd, 2, 8, Api13), Gpu(1, "nv-large", Nvidia, 2, 16, Api13)), "larger device-local heap wins");
            Check(!Allowed("", -1, Gpu(0, "amd-tie", Amd, 2, 8, Api13), Gpu(1, "nv-tie", Nvidia, 2, 8, Api13)), "AMD/NVIDIA tie is conservative");
            bool missing = false;
            try { Allowed("missing", 0, nvidiaDiscrete); } catch (InvalidOperationException) { missing = true; }
            Check(missing, "unknown explicit UUID does not silently select another adapter");
            missing = false;
            try { Allowed("", -1); } catch (InvalidOperationException) { missing = true; }
            Check(missing, "no Vulkan adapters fails detection");
        }

        static void TestLauncher()
        {
            string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            foreach (string marker in new[] { "GTSport.exe", "RyGTS.exe", "RyGT.exe", "RyGT.ini" })
                if (File.Exists(Path.Combine(baseDir, marker)))
                    throw new Exception("Use a separate test output folder, not a launcher installation.");
            if (Directory.Exists(Path.Combine(baseDir, "app0")))
                throw new Exception("Use a separate test output folder, not a game installation.");
            string config = Path.Combine(baseDir, "user", "custom_configs", "CUSA03220.json");
            Check(Path.GetFullPath(config).StartsWith(baseDir, StringComparison.OrdinalIgnoreCase), "fixture config stays under test output");
            Directory.CreateDirectory(Path.GetDirectoryName(config));
            File.WriteAllText(config, "{\"GPU\":{\"compile_mode\":1},\"Vulkan\":{\"gpu_id\":-1}}");
            using (var form = new LoaderForm())
            {
                var mode = (ComboBox)Field(form, "shaderCompileMode");
                Set(form, "GPU", "compile_mode", 2);
                Apply(form, false);
                Check(mode.Items.Count == 2 && mode.SelectedIndex == 1 && Mode(form) == 1, "saved Keep going is replaced and persisted on AMD");

                File.WriteAllText(config, "{\"GPU\":{},\"Vulkan\":{\"gpu_id\":-1}}");
                Check((int)Call(form, "ReadShaderCompilationMode") == 2, "missing policy matches renderer default");
                Apply(form, false);
                Check(mode.Items.Count == 2 && mode.SelectedIndex == 1 && Mode(form) == 1, "missing policy is persisted as Slow motion on AMD");

                Set(form, "GPU", "compile_mode", 3);
                Apply(form, false);
                Check(mode.SelectedIndex == 1 && Mode(form) == 1, "out-of-range Keep going is replaced on AMD");
                Set(form, "GPU", "compile_mode", "invalid");
                Apply(form, false);
                Check(mode.SelectedIndex == 1 && Mode(form) == 1, "invalid policy uses renderer default then falls back");
                foreach (int value in new[] { 0, 1 })
                {
                    Set(form, "GPU", "compile_mode", value);
                    Apply(form, false);
                    Check(mode.SelectedIndex == value && Mode(form) == value, "Pause and Slow motion remain unchanged");
                }
                Set(form, "GPU", "compile_mode", 2);
                Apply(form, true);
                Check(mode.Items.Count == 3 && mode.SelectedIndex == 2 && Mode(form) == 2, "NVIDIA keeps all three policies");
                TestLiveDeviceSwitch(form, mode);
                Set(form, "GPU", "compile_mode", 2);
                Apply(form, true);
                Apply(form, null);
                Check(mode.Items.Count == 3 && mode.SelectedIndex == 2 && Mode(form) == 2, "unknown GPU does not silently rewrite saved choice");
                Set(form, "Vulkan", "device_uuid", "no-such-vulkan-device-for-policy-fixture");
                bool blocked = false;
                try { Call(form, "EnforceShaderCompilationPolicy"); }
                catch (TargetInvocationException e) { blocked = e.InnerException != null; }
                Check(blocked && Mode(form) == 2, "unknown device blocks Keep going before launch");

                FileAttributes attributes = File.GetAttributes(config);
                try
                {
                    File.SetAttributes(config, attributes | FileAttributes.ReadOnly);
                    bool failed = false;
                    try { Apply(form, false); }
                    catch (TargetInvocationException e) { failed = e.InnerException is IOException || e.InnerException is UnauthorizedAccessException; }
                    Check(failed && Mode(form) == 2, "failed fallback write is reported and does not claim success");
                }
                finally { File.SetAttributes(config, attributes); }
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                TestRanking();
                TestLauncher();
                if (args.Length != 0 && args[0] == "--probe")
                {
                    var devices = VulkanDevices.Enumerate();
                    Check(devices.Count > 0, "real Vulkan device enumeration");
                    foreach (var device in devices)
                        Console.WriteLine("Vulkan: index={0} vendor=0x{1:x} type={2} local_bytes={3} name={4}",
                            device.Index, device.VendorId, device.DeviceType, device.DeviceLocalBytes, device.Name);
                }
                Console.WriteLine("PASS: {0} shader compilation policy checks", checks);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
