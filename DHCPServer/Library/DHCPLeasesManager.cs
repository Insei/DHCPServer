using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace GitHub.JPMikkers.DHCP
{
    public class DHCPLeasesManager : IDHCPLeasesManager
    {
        private readonly object _lock = new object();
        private List<DHCPLease> _leases;
        private DHCPPool _pool;
        private Timer _leasesCheckTimer;

        public DHCPPool Pool
        {
            get => _pool;
            set
            {
                if(_pool != value)
                {
                    _pool.LeasesManager = null;
                }

                if (value != null)
                {
                    _pool = value;
                    _pool.LeasesManager = this;
                }
            }
        }

        public TimeSpan LeaseTime { get; set; }
        private void CheckLeases(object state)
        {
            lock (_lock)
            {
                foreach (var lease in _leases.Where(l => l.IsExpired()
                                                         && LeaseTime != TimeSpan.Zero 
                                                         && l.Status != DHCPLeaseStatus.Released))
                {
                    lease.Status = DHCPLeaseStatus.Released;
                    lease.NotifyChange();
                }
            }
        }

        public List<DHCPLease> GetLeases()
        {
            lock (_lock)
            {
                return _leases.Select(lease => lease.Clone()).ToList();
            }
        }

        public event EventHandler<DHCPLease> OnAdd;
        public event EventHandler<DHCPLease> OnLeaseChange;
        public event EventHandler<DHCPLease> OnRemove;

        public DHCPLeasesManager(DHCPPool pool, TimeSpan leaseTime)
        {
            _leases = new List<DHCPLease>();
            _pool = pool;
            _pool.LeasesManager = this;
            _leasesCheckTimer = new Timer(CheckLeases, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            LeaseTime = leaseTime;
        }

        private DHCPLease FindByMac(byte[] macAddress)
        {
            var macString = Utils.BytesToHexString(macAddress, "-");
            lock (_lock)
            {
                return _leases.FirstOrDefault(c => c.MacAddress == macString);
            }
        }

        private void OnLeaseChangeInternal(object sender, object e)
        {
            var lease = (DHCPLease) sender;
            OnLeaseChange?.Invoke(this, lease.Clone());
        }
        
        public DHCPLease Create(byte[] macAddress)
        {
            lock (_lock)
            {
                var protectedLease = new DHCPLease
                {
                    MacAddress = Utils.BytesToHexString(macAddress, "-"),
                    LeaseTime = LeaseTime,
                    Status = DHCPLeaseStatus.Created
                };
                _leases.Add(protectedLease);
            
                return protectedLease.Clone();
            }
        }

        public DHCPLease Get(byte[] macAddress)
        {
            var macAddressString = Utils.BytesToHexString(macAddress, "-");
            lock (_lock)
            {
                var protectedLease = _leases.FirstOrDefault(l => l.MacAddress == macAddressString);
                return protectedLease?.Clone();
            }
        }

        public DHCPLease Get(IPAddress address)
        {
            DHCPLease protectedLease = null;
            lock (_lock)
            {
                if(!address.Equals(IPAddress.Any))
                    protectedLease = _leases.FirstOrDefault(l => l.Address.Equals(address));
            }
            return protectedLease?.Clone();
        }

        public void Update(DHCPLease lease)
        {
            lock (_lock)
            {
                var protectedLease = _leases.FirstOrDefault(l => l.MacAddress == lease.MacAddress);
                if (protectedLease == null)
                    throw new Exception("Lease not found");
            
                switch (protectedLease.Static)
                {
                    case true when !protectedLease.Address.Equals(lease.Address):
                        throw new Exception("Lease is static, can't set another ip");
                    case false:
                        protectedLease.Address = lease.Address;
                        break;
                }

                protectedLease.Options = lease.Options;
                protectedLease.HostName = lease.HostName;
                protectedLease.Status = lease.Status;
                protectedLease.LeaseTime = lease.LeaseTime;
                protectedLease.ClientId = lease.ClientId;
                protectedLease.LeaseTime = LeaseTime;
                
                switch (protectedLease.Status)
                {
                    case DHCPLeaseStatus.Bounded:
                    case DHCPLeaseStatus.Offered:
                        protectedLease.Start = DateTime.Now;
                        protectedLease.End = protectedLease.Start.Add(LeaseTime);
                        if (protectedLease.Address.Equals(IPAddress.Any))
                            protectedLease.Address = Pool.AllocateIPAddress();
                        break;
                    case DHCPLeaseStatus.Created:
                        break;
                    case DHCPLeaseStatus.Released:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                    

                if (!protectedLease.IsSubscribedToLeasesManager())
                {
                    protectedLease.OnChange += OnLeaseChangeInternal;
                    lease = protectedLease.Clone();
                    OnAdd?.Invoke(this, lease);
                }
                else
                    protectedLease.NotifyChange();
            }
        }

        public IPAddress FreeOlderUnusedIp()
        {
            lock (_lock)
            {
                var leaseToRemove = _leases.OrderBy(l => l.End).FirstOrDefault(l => l.Static == false && l.IsExpired());
                if (leaseToRemove == null)
                    return IPAddress.Any;

                _leases.Remove(leaseToRemove);
                leaseToRemove.OnChange -= OnLeaseChangeInternal;

                OnRemove?.Invoke(this, leaseToRemove.Clone());
                return leaseToRemove.Address;
            }
        }

        public void LoadSavedLeases(List<DHCPLease> leases)
        {
            lock (_lock)
            {
                if (_leases.Count == 0)
                {
                    foreach (var lease in leases)
                    {
                        if (!Pool.AllocateIPAddress(lease.Address).Equals(IPAddress.Any))
                        {
                            _leases.Add(lease);
                        }
                    }
                }
            }
        }

        public void Remove(DHCPLease lease)
        {
            lock (_lock)
            {
                var protectedLease = _leases.FirstOrDefault(l => l.MacAddress == lease.MacAddress);
                if(protectedLease == null)
                    throw new Exception("Lease not found");
            
                if (protectedLease.Static)
                    throw new Exception("Can't remove static lease");
            
                _leases.Remove(protectedLease);
                Pool.MarkAsUnused(protectedLease.Address);
                lease = protectedLease.Clone();
            }
            OnRemove?.Invoke(this, lease);
        }

        public void MakeStatic(DHCPLease lease)
        {
            lock (_lock)
            {
                var protectedLease = _leases.FirstOrDefault(l => l.MacAddress == lease.MacAddress);
                if(protectedLease == null)
                    throw new Exception("Lease not found");
            
                if(_leases.Any(l => l.MacAddress != lease.MacAddress && lease.Address.Equals(l.Address) && (l.Static || l.Status != DHCPLeaseStatus.Released)))
                    throw new Exception("Can't make static, because we already have lease with chosen ip address");

                protectedLease.Static = true;
                protectedLease.Address = lease.Address;
                protectedLease.Options = lease.Options;
                if (!protectedLease.IsSubscribedToLeasesManager())
                {
                    protectedLease.OnChange += OnLeaseChangeInternal;
                    lease = protectedLease.Clone();
                    OnAdd?.Invoke(this, lease);
                }
                protectedLease.NotifyChange();
            }
        }
        
        public void MakeDynamic(DHCPLease lease)
        {
            lock (_lock)
            {
                var protectedLease = _leases.FirstOrDefault(l => l.MacAddress == lease.MacAddress);
                if (protectedLease == null)
                    throw new Exception("Lease not found");
                
                protectedLease.Static = false;
                protectedLease.NotifyChange();    
            }
        }
    }
}