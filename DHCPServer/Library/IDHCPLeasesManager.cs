using System;
using System.Collections.Generic;
using System.Net;

namespace GitHub.JPMikkers.DHCP
{
    public interface IDHCPLeasesManager
    {
        event EventHandler<DHCPLease> OnAdd;
        event EventHandler<DHCPLease> OnLeaseChange;
        event EventHandler<DHCPLease> OnRemove;
        TimeSpan LeaseTime { get; set; }
        DHCPPool Pool { get; set; }
        List<DHCPLease> GetLeases();
        DHCPLease Create(byte[] macAddress);
        DHCPLease Get(byte[] macAddress);
        DHCPLease Get(IPAddress address);
        void Update(DHCPLease lease);
        void MakeStatic(DHCPLease lease);
        void MakeDynamic(DHCPLease lease);
        void Remove(DHCPLease lease);
        IPAddress FreeOlderUnusedIp();
    }
}