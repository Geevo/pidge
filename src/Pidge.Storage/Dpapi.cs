using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pidge.Storage;

/// <summary>The Windows Data Protection API, which ties a blob to the user's login.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Dpapi
{
    /// <summary>Fail rather than show anything.</summary>
    private const uint UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public byte* Data;
    }

    /// <exception cref="Win32Exception">DPAPI refused.</exception>
    public static byte[] Protect(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy) => Run(input, entropy, protect: true);

    /// <exception cref="Win32Exception">DPAPI refused.</exception>
    public static byte[] Unprotect(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy) => Run(input, entropy, protect: false);

    private static byte[] Run(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect)
    {
        fixed (byte* inputBytes = input)
        fixed (byte* entropyBytes = entropy)
        {
            // Both input blobs point at pinned spans of the stated length, which
            // DPAPI only reads. On success it allocates the output with
            // LocalAlloc, which is copied out and freed below.
            var inBlob = new Blob { Size = (uint)input.Length, Data = inputBytes };
            var entropyBlob = new Blob { Size = (uint)entropy.Length, Data = entropyBytes };
            var outBlob = default(Blob);

            var ok = protect
                ? CryptProtectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob)
                : CryptUnprotectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>(outBlob.Data, (int)outBlob.Size).ToArray();
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        Blob* dataIn,
        char* description,
        Blob* entropy,
        void* reserved,
        void* prompt,
        uint flags,
        Blob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        Blob* dataIn,
        char** description,
        Blob* entropy,
        void* reserved,
        void* prompt,
        uint flags,
        Blob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial void* LocalFree(void* memory);
}
