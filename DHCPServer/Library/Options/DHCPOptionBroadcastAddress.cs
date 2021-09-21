using System.IO;
using System.Net;

namespace GitHub.JPMikkers.DHCP
{
    public class DHCPOptionBroadcastAddress : DHCPOptionBase
    {
        private IPAddress _broadcastAddress;
        
        public DHCPOptionBroadcastAddress()
            : base(TDHCPOption.BroadcastAddress)
        {
        }
        
        public DHCPOptionBroadcastAddress(IPAddress broadcastAddress)
            : base(TDHCPOption.BroadcastAddress)
        {
            _broadcastAddress = broadcastAddress;
        }

        public IPAddress IPAddress => _broadcastAddress;

        public override IDHCPOption FromStream(Stream s)
        {
            var result = new DHCPOptionBroadcastAddress();
            if (s.Length != 4) throw new IOException("Invalid DHCP option length");
            result._broadcastAddress = ParseHelper.ReadIPAddress(s);
            return result;
        }

        public override void ToStream(Stream s)
        {
            ParseHelper.WriteIPAddress(s, _broadcastAddress);
        }
        
        public override string ToString()
        {
            return $"Option(name=[{OptionType}],value=[{_broadcastAddress}])";
        }
    }
}