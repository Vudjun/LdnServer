﻿using Ryujinx.HLE.HOS.Services.Ldn.Types;
using System.Runtime.InteropServices;

namespace LanPlayServer.Network.Types
{
    [StructLayout(LayoutKind.Sequential, Size = 0x13C, Pack = 1)]
    public struct CreateAccessPointPrivateRequest
    {
        public SecurityConfig SecurityConfig;
        public SecurityParameter SecurityParameter;
        public UserConfig UserConfig;
        public NetworkConfig NetworkConfig;
        public AddressList AddressList;

        public RyuNetworkConfig RyuNetworkConfig;

        // Advertise data is appended separately. (remaining data in the buffer)
    }
}