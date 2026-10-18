using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// Names the DirectML / DXGI adapters so that a log line can say which GPU a <c>deviceId</c> selects.
/// DirectML picks adapters in DXGI enumeration order, so the index used here is the same number handed to
/// <c>AppendExecutionProvider_DML</c>. Best effort only: it returns nothing when dxgi.dll or a call is
/// unavailable and never throws; it exists purely to make the device log readable.
/// </summary>
internal static class DirectMlAdapters
{
    private static readonly Guid IID_IDXGIFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    // DXGI_ADAPTER_DESC1 starts with the inline name: WCHAR Description[128], then 4 UINTs, 3 SIZE_T, an 8-byte
    // LUID and a UINT. Read through a raw buffer because marshalling the struct with its inline string is fiddly.
    private const int AdapterDescSize = 128 * 2 + 4 + 4 + 4 + 4 + 8 + 8 + 8 + 8 + 4; // 308, padded to 312
    private const int DescriptionOffset = 0;
    private const int VendorIdOffset = 256;
    private const int DeviceIdOffset = 260;
    private const int DedicatedVideoMemoryOffset = 272;
    private const int FlagsOffset = 304;
    private const int SoftwareFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

    // Enumerated once per process; the adapter set does not change while the app runs.
    private static readonly IReadOnlyList<Adapter> All = Enumerate();

    /// <summary>A one-line description of the adapter the given device id selects, for a log line.</summary>
    public static string Describe(int deviceId)
    {
        if (deviceId < 0) return "CPU (no DirectML device)";
        if (deviceId < All.Count) return All[deviceId].ToString();
        string known = All.Count == 0 ? "no adapters enumerated" : $"{All.Count} adapters enumerated";
        return $"deviceId {deviceId} (no DXGI adapter at that index; {known})";
    }

    /// <summary>Every enumerated adapter, or "none detected".</summary>
    public static string Summary() => All.Count == 0 ? "none detected" : string.Join("; ", All);

    private static IReadOnlyList<Adapter> Enumerate()
    {
        var adapters = new List<Adapter>();
        IntPtr factory = IntPtr.Zero;
        IntPtr desc = IntPtr.Zero;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return adapters;

            desc = Marshal.AllocHGlobal(AdapterDescSize + 8);
            var enumAdapters1 = GetDelegate<EnumAdapters1Delegate>(factory, 12); // IDXGIFactory1::EnumAdapters1
            for (uint index = 0; ; index++)
            {
                if (enumAdapters1(factory, index, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    var getDesc1 = GetDelegate<GetDesc1Delegate>(adapter, 10); // IDXGIAdapter1::GetDesc1
                    if (getDesc1(adapter, desc) < 0) continue;

                    adapters.Add(new Adapter(
                        index,
                        Marshal.PtrToStringUni(IntPtr.Add(desc, DescriptionOffset)) ?? string.Empty,
                        (uint)Marshal.ReadInt32(desc, VendorIdOffset),
                        (uint)Marshal.ReadInt32(desc, DeviceIdOffset),
                        (ulong)Marshal.ReadInt64(desc, DedicatedVideoMemoryOffset),
                        (Marshal.ReadInt32(desc, FlagsOffset) & SoftwareFlag) != 0));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        catch
        {
            // Adapter names only decorate a log line; never let them break embedding.
        }
        finally
        {
            if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc);
            if (factory != IntPtr.Zero) Release(factory);
        }
        return adapters;
    }

    private static int Release(IntPtr comObject) => GetDelegate<ReleaseDelegate>(comObject, 2)(comObject);

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Delegate(IntPtr self, uint adapter, out IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Delegate(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReleaseDelegate(IntPtr self);

    private readonly record struct Adapter(uint Index, string Name, uint VendorId, uint DeviceId, ulong DedicatedVideoMemory, bool IsSoftware)
    {
        public override string ToString() =>
            $"{Index}: {Name} (vendor 0x{VendorId:X4}, device 0x{DeviceId:X4}, vram {DedicatedVideoMemory / (1024 * 1024)} MiB{(IsSoftware ? ", software" : string.Empty)})";
    }
}
