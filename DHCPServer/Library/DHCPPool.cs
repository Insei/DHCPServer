using System.Collections.Generic;
using System.Linq;
using System.Net;
using NetTools;

namespace GitHub.JPMikkers.DHCP
{
    public class DHCPPool
    {
        private readonly object _lock = new object();
        private List<IPAddress> _pool;
        private List<IPAddress> _unused;

        public IDHCPLeasesManager LeasesManager { get; set; }

        public void RemoveFromUnused(List<IPAddress> list)
        {
            lock(_lock)
                _unused.RemoveAll(i => list.Any(ip => ip.Equals(i)));
        }

        public DHCPPool(string pool)
        {
            _pool = IPAddressRange.Parse(pool).AsEnumerable().ToList();
            _unused = IPAddressRange.Parse(pool).AsEnumerable().ToList();
        }

        public IPAddress AllocateIPAddress(IPAddress ipAddress)
        {
            lock (_lock)
            {
                
                if (!ipAddress.Equals(IPAddress.Any))
                {
                    var address = _unused.FirstOrDefault(i => i.Equals(ipAddress));
                    if (address != null)
                    {
                        _unused.Remove(address);
                        return address;
                    }

                    if (LeasesManager != null)
                    {
                        var lease = LeasesManager.Get(ipAddress);
                        //I think this situation is can be, and we need handle this correctly
                        if (lease == null)
                            return ipAddress;
                    
                        if (!lease.Static && lease.Status == DHCPLeaseStatus.Released)
                        {
                            LeasesManager.Remove(lease);
                            return lease.Address;
                        }
                    }
                }

                return IPAddress.Any;
            }
        }

        public IPAddress AllocateIPAddress()
        {
            lock (_lock)
            {
                var ipAddress = _unused.FirstOrDefault();
                if (ipAddress != null)
                {
                    _unused.Remove(ipAddress);
                    return ipAddress;
                }

                return LeasesManager.FreeOlderUnusedIp();
            }
        }

        public void MarkAsUnused(IPAddress ipAddress)
        {
            if (ipAddress == null || ipAddress.Equals(IPAddress.Any))
                return;
            lock (_lock)
            {
                var inPool = _pool.Any(i => i.Equals(ipAddress));
                if(inPool && !_unused.Any(i => i.Equals(ipAddress)))
                    _unused.Add(ipAddress);
            }
        }

        public bool InPool(IPAddress ipAddress)
        {
            if (ipAddress == null || ipAddress.Equals(IPAddress.Any))
                return false;
            lock (_lock)
            {
                return _pool.Any(i => i.Equals(ipAddress));
            }
        }
    }
}