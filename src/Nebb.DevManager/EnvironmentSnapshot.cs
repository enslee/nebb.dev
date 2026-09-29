using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Nebb.DevManager;

internal static class EnvironmentSnapshot
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(IReadOnlyList<EnvironmentEntry> entries) =>
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries)),
            protect: true));

    public static IReadOnlyList<EnvironmentEntry> Unprotect(string value) =>
        JsonSerializer.Deserialize<List<EnvironmentEntry>>(
            Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), protect: false))) ?? [];

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Marshal.Copy(value, 0, input.Data, value.Length);
        try
        {
            Blob output;
            var result = protect
                ? CryptProtectData(ref input, "Nebb process environment", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(),
                "실행 환경 정보를 보호하거나 읽을 수 없습니다.");
            try
            {
                var bytes = new byte[output.Length];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
