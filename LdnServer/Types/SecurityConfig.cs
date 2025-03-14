using LanPlayServer.Utils;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Ldn.Types
{
    [StructLayout(LayoutKind.Sequential, Size = 0x44, Pack = 2)]
    public struct SecurityConfig
    {
        public SecurityMode  SecurityMode;
        public ushort        PassphraseSize;
        public Array64<byte> Passphrase;
    }
}