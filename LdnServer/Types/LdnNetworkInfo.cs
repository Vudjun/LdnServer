using LanPlayServer.Utils;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Ldn.Types
{
    [StructLayout(LayoutKind.Sequential, Size = 0x430)]
    public struct LdnNetworkInfo
    {
        public Array16<byte>    SecurityParameter;
        public ushort           SecurityMode;
        public byte             StationAcceptPolicy;
        public byte             Unknown1;
        public ushort           Reserved1;
        public byte             NodeCountMax;
        public byte             NodeCount;
        public Array8<NodeInfo> Nodes;
        public ushort           Reserved2;
        public ushort           AdvertiseDataSize;
        public Array384<byte>   AdvertiseData;
        public Array140<byte>   Unknown2;
        public ulong            AuthenticationId;
    }
}