param(
    [string]$Configuration = 'Release',
    [string]$Executable = '',
    [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $root "src/LiveCaptions.Desktop/bin/$Configuration/net8.0-windows10.0.19041.0/win-x64/LiveCaptions.Enhanced.exe" }
if (!$Report) { $Report = Join-Path $root 'artifacts/winui-test.json' }
$Executable = [IO.Path]::GetFullPath($Executable)
$Report = [IO.Path]::GetFullPath($Report)
if (!$Executable.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    !$Report.StartsWith((Join-Path $root 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Executable and report must stay in this workspace.'
}
# A packaged terminal can pass its MSIX identity to children. Use the documented
# desktop-app launch policy on a broker so the tested app has its normal identity.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class WinUiTestLaunch
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string reserved, desktop, title; public int x,y,cx,cy,charsX,charsY,fill,flags; public short show,reserved2; public IntPtr bytes,input,output,error; }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO si; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESSINFO { public IntPtr process,thread; public int id,threadId; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref STARTUPINFOEX startup,out PROCESSINFO process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out int code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static int Run(string executable,string report,string directory)
    {
        IntPtr size=IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
        IntPtr list=Marshal.AllocHGlobal(size), policy=Marshal.AllocHGlobal(4);
        PROCESSINFO process=new PROCESSINFO();
        try {
            if(!InitializeProcThreadAttributeList(list,1,0,ref size)) throw new Win32Exception();
            Marshal.WriteInt32(policy,1);
            if(!UpdateProcThreadAttribute(list,0,new IntPtr(0x20012),policy,new IntPtr(4),IntPtr.Zero,IntPtr.Zero)) throw new Win32Exception();
            var startup=new STARTUPINFOEX();
            startup.si.cb=Marshal.SizeOf(typeof(STARTUPINFOEX)); startup.si.flags=1; startup.si.show=0; startup.attributes=list;
            string cmd=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
            var command=new StringBuilder("\"" + cmd + "\" /d /s /c \"\"" + executable + "\" --self-test \"" + report + "\"\"");
            if(!CreateProcess(cmd,command,IntPtr.Zero,IntPtr.Zero,false,0x80000|0x08000000,IntPtr.Zero,directory,ref startup,out process)) throw new Win32Exception();
            if(WaitForSingleObject(process.process,45000)!=0) throw new TimeoutException("WinUI test timed out.");
            int code; GetExitCodeProcess(process.process,out code); return code;
        }
        finally {
            if(process.thread!=IntPtr.Zero) CloseHandle(process.thread);
            if(process.process!=IntPtr.Zero) CloseHandle(process.process);
            DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); Marshal.FreeHGlobal(policy);
        }
    }
}
'@
if (Test-Path -LiteralPath $Report) { Remove-Item -LiteralPath $Report }
$code = [WinUiTestLaunch]::Run($Executable, $Report, $root)
if (Test-Path -LiteralPath $Report) { Get-Content -LiteralPath $Report }
if ($code -ne 0 -or !(Test-Path -LiteralPath $Report)) { throw "WinUI 3 self-test failed ($code)." }

