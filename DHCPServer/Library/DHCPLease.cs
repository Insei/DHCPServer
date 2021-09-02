using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace GitHub.JPMikkers.DHCP
{
    public enum DHCPLeaseStatus
    {
        Created,
        Offered,
        Bounded,
        Released
    }

    public class DHCPLease
    {
        private DHCPLeaseStatus _status;

        public bool Static { get; set; }
        public IPAddress Address { get; set; }
        public string MacAddress { get; set; }
        public string ClientId { get; set; }
        public string HostName { get; set; }
        public DateTime Start { get; set; }

        public DateTime End { get; set; }

        public TimeSpan LeaseTime { get; set; }

        public bool IsSubscribedToLeasesManager()
        {
            return OnChange != null;
        }

        public DHCPLeaseStatus Status
        {
            get => _status;
            set => _status = value;
        }
        
        public List<OptionItem> Options { get; set; }
        
        public event EventHandler OnChange;

        public DHCPLease()
        {
            Address = IPAddress.Any;
            Options = new List<OptionItem>();
            _status = DHCPLeaseStatus.Created;
        }

        public void UpdateFromMessage(DHCPMessage message)
        {
            MacAddress = Utils.BytesToHexString(message.ClientHardwareAddress, "-");

            var dhcpOptionHostName = (DHCPOptionHostName)message.GetOption(TDHCPOption.HostName);
            if (dhcpOptionHostName != null)
            {
                HostName = dhcpOptionHostName.HostName;
            }

            var dhcpOptionClientIdentifier = (DHCPOptionClientIdentifier)message.GetOption(TDHCPOption.ClientIdentifier);

            if (dhcpOptionClientIdentifier != null)
            {
                ClientId = dhcpOptionClientIdentifier.Data.ToString();
            }
            else
            {
                ClientId = MacAddress;
            }
        }

        public bool IsExpired()
        {
            return End.ToUniversalTime() < DateTime.Now.ToUniversalTime();
        }
        
        public void NotifyChange()
        {
            OnChange?.Invoke(this, EventArgs.Empty);
        }

        public DHCPLease Clone()
        {
            var lease = new DHCPLease()
            {
                Address = Address,
                End = End,
                Options = Options,
                Static = Static,
                Start = Start,
                Status = Status,
                ClientId = ClientId,
                HostName = HostName,
                LeaseTime = LeaseTime,
                MacAddress = MacAddress
            };
            return lease;
        }
    }
}