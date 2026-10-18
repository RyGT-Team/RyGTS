#include "SceTypes.hpp"
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <initializer_list>

static void Check(bool ok, const char* why) { if (!ok) { std::fprintf(stderr,"FAIL: %s (Windows %lu)\n",why,GetLastError()); std::exit(1); } }
static std::int64_t Seconds(const SYSTEMTIME& t) { FILETIME f{}; Check(SystemTimeToFileTime(&t,&f)!=0,"calendar"); return static_cast<std::int64_t>(((std::uint64_t(f.dwHighDateTime)<<32)|f.dwLowDateTime)/10000000)-11644473600LL; }
int main(int argc,char** argv) {
    if(argc!=3) return 2;
    SetDllDirectoryA(argv[2]);
    HMODULE m=LoadLibraryExA(argv[1],nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
    Check(m!=nullptr,"load actual libkernel build");
    using UtcFn=int(APS5_VABI *)(std::int64_t,std::int64_t*,KernelTimesec*,std::uint64_t*);
    using LocalFn=int(APS5_VABI *)(std::int64_t,std::int64_t,std::int64_t*,KernelTimezone*,std::int32_t*);
    using TzFn=int(APS5_VABI *)(KernelTimezone*);
    using NowFn=int(APS5_VABI *)(KernelTimeval*,KernelTimezone*);
    auto utc=reinterpret_cast<UtcFn>(GetProcAddress(m,"sceKernelConvertUtcToLocaltime"));
    auto local=reinterpret_cast<LocalFn>(GetProcAddress(m,"sceKernelConvertLocaltimeToUtc"));
    auto zone=reinterpret_cast<TzFn>(GetProcAddress(m,"sceKernelGettimezone"));
    auto now=reinterpret_cast<NowFn>(GetProcAddress(m,"gettimeofday_nid_postfix"));
    Check(utc&&local&&zone&&now,"clock exports");
    for(unsigned month : {1u,7u}) {
        SYSTEMTIME universal{}; universal.wYear=2026; universal.wMonth=WORD(month); universal.wDay=15; universal.wHour=12;
        SYSTEMTIME expected{}; Check(SystemTimeToTzSpecificLocalTimeEx(nullptr,&universal,&expected)!=0,"Windows reference conversion");
        const auto input=Seconds(universal);
        std::int64_t result{}, back{}; KernelTimesec info{}; std::uint64_t daylight{}; KernelTimezone tz{}; std::int32_t inverseDaylight{};
        Check(utc(input,&result,&info,&daylight)==0,"UTC conversion result");
        Check(result==Seconds(expected),"January/July local time matches Windows");
        Check(info.t==input && result-input==static_cast<std::int32_t>(info.west_sec)+static_cast<std::int32_t>(info.dst_sec),"offset metadata");
        Check(local(result,0,&back,&tz,&inverseDaylight)==0 && back==input,"local to UTC roundtrip");
        Check(result-input==60LL*(tz.tz_minuteswest+tz.tz_dsttime) && inverseDaylight==static_cast<std::int32_t>(daylight),"timezone metadata");
        Check(utc(input,nullptr,nullptr,nullptr)==0 && local(result,0,nullptr,nullptr,nullptr)==0,"optional outputs");
        std::printf("2026-%02u: local offset %lld seconds, DST %lld seconds\n",month,static_cast<long long>(result-input),static_cast<long long>(daylight));
    }
    KernelTimeval current{}; KernelTimezone tz{}, reported{}; std::int64_t displayed{};
    Check(now(&current,&tz)==0 && zone(&reported)==0,"current time and timezone");
    Check(utc(current.tv_sec,&displayed,nullptr,nullptr)==0,"current local conversion");
    Check(displayed-current.tv_sec==60LL*(tz.tz_minuteswest+tz.tz_dsttime),"current timezone agrees");
    Check(tz.tz_minuteswest==reported.tz_minuteswest && tz.tz_dsttime==reported.tz_dsttime,"timezone APIs agree");
    Check(zone(nullptr)==static_cast<int>(0x8002000e),"null timezone rejected");
    Check(utc(std::numeric_limits<std::int64_t>::max(),nullptr,nullptr,nullptr)==static_cast<int>(0x80020016),"overflow input rejected");
    SYSTEMTIME systemLocal{}; GetLocalTime(&systemLocal);
    const auto difference=displayed-Seconds(systemLocal); Check(difference>=-2 && difference<=2,"display clock matches Windows local time");
    std::printf("PASS: Windows local time %02u:%02u; UTC timestamp preserved; timezone/DST/roundtrip/error checks\n",systemLocal.wHour,systemLocal.wMinute);
}