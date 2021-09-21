/*

Copyright (c) 2010 Jean-Paul Mikkers

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

*/
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Net.NetworkInformation;
//using System.Net.Configuration;
using System.Threading;
using System.Linq;

namespace GitHub.JPMikkers.DHCP
{
    public class DHCPServer : IDHCPServer
    {
        private readonly object _lock = new object();
        private readonly object _leasesSync = new object();
        
        private IPEndPoint _endPoint = new IPEndPoint(IPAddress.Loopback,67);
        private UDPSocket _socket;
        private bool _active = false;
        private List<OptionItem> _options = new List<OptionItem>();
        private int _minimumPacketSize = 576;
        
        private DHCPOptionBroadcastAddress _broadcastAddressOption;
        private DHCPOptionServerIdentifier _serverIdentifierOption;

        #region IDHCPServer Members

        public event EventHandler<DHCPTraceEventArgs> OnTrace = delegate(object sender, DHCPTraceEventArgs args) { };
        public event EventHandler<DHCPStopEventArgs> OnStatusChange = delegate(object sender, DHCPStopEventArgs args) { };
        public IDHCPLeasesManager LeasesManager { get; }

        public IPEndPoint EndPoint
        {
            get => _endPoint;
            set => _endPoint = value;
        }

        public int MinimumPacketSize
        {
            get => _minimumPacketSize;
            set => _minimumPacketSize = Math.Max(value,312);
        }

        public IList<DHCPLease> Leases => LeasesManager.GetLeases();

        public bool Active
        {
            get
            {
                lock (_lock)
                {
                    return _active;
                }
            }
        }

        public List<OptionItem> Options
        {
            get => _options;
            set => _options = value;
        }

        public DHCPServer(IPAddress host, int port, IDHCPLeasesManager leasesManager, List<OptionItem> options)
        {
            _endPoint = new IPEndPoint(host, port);
            _options = options;
            LeasesManager = leasesManager;
            
            DetermineServerIdentifier();
            DetermineBroadcastAddress();
        }

        private void DetermineServerIdentifier()
        {
            var serverIdentifier = _options.FirstOrDefault(o => o.Option.OptionType == TDHCPOption.ServerIdentifier);
            if (serverIdentifier.Option != null)
                _serverIdentifierOption = (DHCPOptionServerIdentifier)serverIdentifier.Option;
            else
                _serverIdentifierOption = new DHCPOptionServerIdentifier(((IPEndPoint)_socket.LocalEndPoint).Address);
        }
        
        private void DetermineBroadcastAddress()
        {
            var broadcastAddress = _options.FirstOrDefault(o => o.Option.OptionType == TDHCPOption.BroadcastAddress);
            if (broadcastAddress.Option != null)
                _broadcastAddressOption = (DHCPOptionBroadcastAddress)broadcastAddress.Option;
            else
                _broadcastAddressOption =  new DHCPOptionBroadcastAddress(IPAddress.Broadcast);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (!_active)
                {
                    try
                    {
                        Trace(string.Format("Starting DHCP server '{0}'", _endPoint));
                        _active = true;
                        _socket = new UDPSocket(_endPoint, 2048, true, 10, OnReceive, OnStop);
                        Trace("DHCP Server start succeeded");
                    }
                    catch (Exception e)
                    {
                        Trace(string.Format("DHCP Server start failed, reason '{0}'", e));
                        _active = false;
                        throw;
                    }
                }
            }

            HandleStatusChange(null);
        }

        public void Stop()
        {
            Stop(null);
        }

        #endregion

        #region Dispose pattern

        ~DHCPServer()
        {
            try
            {
                Dispose(false);
            }
            catch
            {
                // never let any exception escape the finalizer, or else your process will be killed.
            }
        }

        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
            }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }

        #endregion

        private void HandleStatusChange(DHCPStopEventArgs data)
        {
            OnStatusChange(this, data);            
        }

        internal void Trace(string msg)
        {
            DHCPTraceEventArgs data = new DHCPTraceEventArgs();
            data.Message = msg;
            OnTrace(this, data);
        }

        private void Stop(Exception reason)
        {
            bool notify = false;

            lock (_lock)
            {
                if (_active)
                {
                    Trace(string.Format("Stopping DHCP server '{0}'", _endPoint));
                    _active = false;
                    notify = true;
                    _socket.Dispose();
                    Trace("Stopped");
                }
            }

            if (notify)
            {
                DHCPStopEventArgs data = new DHCPStopEventArgs();
                data.Reason = reason;
                HandleStatusChange(data);
            }
        }

        private void SendMessage(DHCPMessage msg, IPEndPoint endPoint)
        {
            Trace(string.Format("==== Sending response to {0} ====", endPoint));
            Trace(Utils.PrefixLines(msg.ToString(), "s->c "));
            var m = new MemoryStream();
            msg.ToStream(m, _minimumPacketSize);
            _socket.Send(endPoint, new ArraySegment<byte>(m.ToArray()));
        }

        protected virtual void ProcessingReceiveMessage(DHCPMessage sourceMsg, DHCPMessage targetMsg)
        {

        }
        private void AppendConfiguredOptions(DHCPMessage sourceMsg,DHCPMessage targetMsg)
        {
            foreach (var optionItem in _options.Where(optionItem => optionItem.Mode == OptionMode.Force 
                                                      || sourceMsg.IsRequestedParameter(optionItem.Option.OptionType))
                                                .Where(optionItem => targetMsg.GetOption(optionItem.Option.OptionType)==null))
            {
                targetMsg.Options.Add(optionItem.Option);
            }

            ProcessingReceiveMessage(sourceMsg, targetMsg);
        }

        private void SendOFFER(DHCPMessage sourceMsg, DHCPLease lease)
        {
            //Field      DHCPOFFER            
            //-----      ---------            
            //'op'       BOOTREPLY            
            //'htype'    (From "Assigned Numbers" RFC)
            //'hlen'     (Hardware address length in octets)
            //'hops'     0                    
            //'xid'      'xid' from client DHCPDISCOVER message              
            //'secs'     0                    
            //'ciaddr'   0                    
            //'yiaddr'   IP address offered to client            
            //'siaddr'   IP address of next bootstrap server     
            //'flags'    'flags' from client DHCPDISCOVER message              
            //'giaddr'   'giaddr' from client DHCPDISCOVER message              
            //'chaddr'   'chaddr' from client DHCPDISCOVER message              
            //'sname'    Server host name or options           
            //'file'     Client boot file name or options      
            //'options'  options              
            DHCPMessage response = new DHCPMessage();
            response.Opcode = DHCPMessage.TOpcode.BootReply;
            response.HardwareType = sourceMsg.HardwareType;
            response.Hops = 0;
            response.XID = sourceMsg.XID;
            response.Secs = 0;
            response.ClientIPAddress = IPAddress.Any;
            response.YourIPAddress = lease.Address;
            response.NextServerIPAddress = IPAddress.Any;
            response.BroadCast = sourceMsg.BroadCast;
            response.RelayAgentIPAddress = sourceMsg.RelayAgentIPAddress;
            response.ClientHardwareAddress = sourceMsg.ClientHardwareAddress;
            response.MessageType = TDHCPMessageType.OFFER;

            //Option                    DHCPOFFER    
            //------                    ---------    
            //Requested IP address      MUST NOT     : ok
            //IP address lease time     MUST         : ok                                               
            //Use 'file'/'sname' fields MAY          
            //DHCP message type         DHCPOFFER    : ok
            //Parameter request list    MUST NOT     : ok
            //Message                   SHOULD       
            //Client identifier         MUST NOT     : ok
            //Vendor class identifier   MAY          
            //Server identifier         MUST         : ok
            //Maximum message size      MUST NOT     : ok
            //All others                MAY          

            response.Options.Add(new DHCPOptionIPAddressLeaseTime(lease.LeaseTime));
            response.Options.Add(_serverIdentifierOption);
            AppendConfiguredOptions(sourceMsg, response);
            SendOfferOrAck(sourceMsg, response);
        }

        private void SendNAK(DHCPMessage sourceMsg)
        {
            //Field      DHCPNAK
            //-----      -------
            //'op'       BOOTREPLY
            //'htype'    (From "Assigned Numbers" RFC)
            //'hlen'     (Hardware address length in octets)
            //'hops'     0
            //'xid'      'xid' from client DHCPREQUEST message
            //'secs'     0
            //'ciaddr'   0
            //'yiaddr'   0
            //'siaddr'   0
            //'flags'    'flags' from client DHCPREQUEST message
            //'giaddr'   'giaddr' from client DHCPREQUEST message
            //'chaddr'   'chaddr' from client DHCPREQUEST message
            //'sname'    (unused)
            //'file'     (unused)
            //'options'  
            DHCPMessage response = new DHCPMessage();
            response.Opcode = DHCPMessage.TOpcode.BootReply;
            response.HardwareType = sourceMsg.HardwareType;
            response.Hops = 0;
            response.XID = sourceMsg.XID;
            response.Secs = 0;
            response.ClientIPAddress = IPAddress.Any;
            response.YourIPAddress = IPAddress.Any;
            response.NextServerIPAddress = IPAddress.Any;
            response.BroadCast = sourceMsg.BroadCast;
            response.RelayAgentIPAddress = sourceMsg.RelayAgentIPAddress;
            response.ClientHardwareAddress = sourceMsg.ClientHardwareAddress;
            response.MessageType = TDHCPMessageType.NAK;
            response.Options.Add(_serverIdentifierOption);
            if (sourceMsg.IsRequestedParameter(TDHCPOption.SubnetMask))
            {
                var subMaskOption = _options.FirstOrDefault(o => o.Option.OptionType == TDHCPOption.SubnetMask);
                if(subMaskOption.Option != null)
                    response.Options.Add(subMaskOption.Option);
            }


            if (!sourceMsg.RelayAgentIPAddress.Equals(IPAddress.Any))
            {
                // If the 'giaddr' field in a DHCP message from a client is non-zero,
                // the server sends any return messages to the 'DHCP server' port on the
                // BOOTP relay agent whose address appears in 'giaddr'.
                SendMessage(response, new IPEndPoint(sourceMsg.RelayAgentIPAddress, 67));
            }
            else
            {
                // In all cases, when 'giaddr' is zero, the server broadcasts any DHCPNAK
                // messages to 0xffffffff.
                SendMessage(response, new IPEndPoint(IPAddress.Broadcast,68));
            }
        }

        private void SendACK(DHCPMessage sourceMsg, DHCPLease lease)
        {
            //Field      DHCPACK             
            //-----      -------             
            //'op'       BOOTREPLY           
            //'htype'    (From "Assigned Numbers" RFC)
            //'hlen'     (Hardware address length in octets)
            //'hops'     0                   
            //'xid'      'xid' from client DHCPREQUEST message             
            //'secs'     0                   
            //'ciaddr'   'ciaddr' from DHCPREQUEST or 0
            //'yiaddr'   IP address assigned to client
            //'siaddr'   IP address of next bootstrap server
            //'flags'    'flags' from client DHCPREQUEST message             
            //'giaddr'   'giaddr' from client DHCPREQUEST message             
            //'chaddr'   'chaddr' from client DHCPREQUEST message             
            //'sname'    Server host name or options
            //'file'     Client boot file name or options
            //'options'  options
            DHCPMessage response = new DHCPMessage();
            response.Opcode = DHCPMessage.TOpcode.BootReply;
            response.HardwareType = sourceMsg.HardwareType;
            response.Hops = 0;
            response.XID = sourceMsg.XID;
            response.Secs = 0;
            response.ClientIPAddress = sourceMsg.ClientIPAddress;
            response.YourIPAddress = lease.Address;
            response.NextServerIPAddress = IPAddress.Any;
            response.BroadCast = sourceMsg.BroadCast;
            response.RelayAgentIPAddress = sourceMsg.RelayAgentIPAddress;
            response.ClientHardwareAddress = sourceMsg.ClientHardwareAddress;
            response.MessageType = TDHCPMessageType.ACK;

            //Option                    DHCPACK            
            //------                    -------            
            //Requested IP address      MUST NOT           : ok
            //IP address lease time     MUST (DHCPREQUEST) : ok
            //Use 'file'/'sname' fields MAY                
            //DHCP message type         DHCPACK            : ok
            //Parameter request list    MUST NOT           : ok
            //Message                   SHOULD             
            //Client identifier         MUST NOT           : ok
            //Vendor class identifier   MAY                
            //Server identifier         MUST               : ok
            //Maximum message size      MUST NOT           : ok  
            //All others                MAY                

            response.Options.Add(new DHCPOptionIPAddressLeaseTime(lease.LeaseTime));
            response.Options.Add(_serverIdentifierOption);
            if (sourceMsg.IsRequestedParameter(TDHCPOption.SubnetMask))
            {
                var subMaskOption = _options.FirstOrDefault(o => o.Option.OptionType == TDHCPOption.SubnetMask);
                if(subMaskOption.Option != null)
                    response.Options.Add(subMaskOption.Option);
            }
            AppendConfiguredOptions(sourceMsg, response);
            SendOfferOrAck(sourceMsg, response);
        }

        private void SendINFORMACK(DHCPMessage sourceMsg)
        {
            // The server responds to a DHCPINFORM message by sending a DHCPACK
            // message directly to the address given in the 'ciaddr' field of the
            // DHCPINFORM message.  The server MUST NOT send a lease expiration time
            // to the client and SHOULD NOT fill in 'yiaddr'.  The server includes
            // other parameters in the DHCPACK message as defined in section 4.3.1.

            //Field      DHCPACK             
            //-----      -------             
            //'op'       BOOTREPLY           
            //'htype'    (From "Assigned Numbers" RFC)
            //'hlen'     (Hardware address length in octets)
            //'hops'     0                   
            //'xid'      'xid' from client DHCPREQUEST message             
            //'secs'     0                   
            //'ciaddr'   'ciaddr' from DHCPREQUEST or 0
            //'yiaddr'   IP address assigned to client
            //'siaddr'   IP address of next bootstrap server
            //'flags'    'flags' from client DHCPREQUEST message             
            //'giaddr'   'giaddr' from client DHCPREQUEST message             
            //'chaddr'   'chaddr' from client DHCPREQUEST message             
            //'sname'    Server host name or options
            //'file'     Client boot file name or options
            //'options'  options
            DHCPMessage response = new DHCPMessage();
            response.Opcode = DHCPMessage.TOpcode.BootReply;
            response.HardwareType = sourceMsg.HardwareType;
            response.Hops = 0;
            response.XID = sourceMsg.XID;
            response.Secs = 0;
            response.ClientIPAddress = sourceMsg.ClientIPAddress;
            response.YourIPAddress = IPAddress.Any;
            response.NextServerIPAddress = IPAddress.Any;
            response.BroadCast = sourceMsg.BroadCast;
            response.RelayAgentIPAddress = sourceMsg.RelayAgentIPAddress;
            response.ClientHardwareAddress = sourceMsg.ClientHardwareAddress;
            response.MessageType = TDHCPMessageType.ACK;

            //Option                    DHCPACK            
            //------                    -------            
            //Requested IP address      MUST NOT              : ok
            //IP address lease time     MUST NOT (DHCPINFORM) : ok
            //Use 'file'/'sname' fields MAY                
            //DHCP message type         DHCPACK               : ok
            //Parameter request list    MUST NOT              : ok
            //Message                   SHOULD             
            //Client identifier         MUST NOT              : ok
            //Vendor class identifier   MAY                
            //Server identifier         MUST                  : ok
            //Maximum message size      MUST NOT              : ok
            //All others                MAY                
            
            response.Options.Add(_serverIdentifierOption);
            if (sourceMsg.IsRequestedParameter(TDHCPOption.SubnetMask))
            {
                var subMaskOption = _options.FirstOrDefault(o => o.Option.OptionType == TDHCPOption.SubnetMask);
                if(subMaskOption.Option != null)
                    response.Options.Add(subMaskOption.Option);
            }
            AppendConfiguredOptions(sourceMsg, response);
            SendMessage(response, new IPEndPoint(sourceMsg.ClientIPAddress, 68));
        }

        private void SendOfferOrAck(DHCPMessage request, DHCPMessage response)
        {
            // RFC2131.txt, 4.1, paragraph 4

            // DHCP messages broadcast by a client prior to that client obtaining
            // its IP address must have the source address field in the IP header
            // set to 0.

            if (!request.RelayAgentIPAddress.Equals(IPAddress.Any))
            {
                // If the 'giaddr' field in a DHCP message from a client is non-zero,
                // the server sends any return messages to the 'DHCP server' port on the
                // BOOTP relay agent whose address appears in 'giaddr'.
                SendMessage(response, new IPEndPoint(request.RelayAgentIPAddress, 67));
            }
            else
            {
                if (!request.ClientIPAddress.Equals(IPAddress.Any))
                {
                    // If the 'giaddr' field is zero and the 'ciaddr' field is nonzero, then the server
                    // unicasts DHCPOFFER and DHCPACK messages to the address in 'ciaddr'.
                    SendMessage(response, new IPEndPoint(request.ClientIPAddress, 68));
                }
                else
                {
                    // If 'giaddr' is zero and 'ciaddr' is zero, and the broadcast bit is
                    // set, then the server broadcasts DHCPOFFER and DHCPACK messages to
                    // 0xffffffff. If the broadcast bit is not set and 'giaddr' is zero and
                    // 'ciaddr' is zero, then the server unicasts DHCPOFFER and DHCPACK
                    // messages to the client's hardware address and 'yiaddr' address.
                    
                    SendMessage(response, new IPEndPoint(_broadcastAddressOption.IPAddress, 68));
                }
            }
        }

        private bool ServerIdentifierPrecondition(DHCPMessage msg)
        {
            var result = false;
            var dhcpOptionServerIdentifier = (DHCPOptionServerIdentifier)msg.GetOption(TDHCPOption.ServerIdentifier);

            if (dhcpOptionServerIdentifier != null)
            {
                if (dhcpOptionServerIdentifier.IPAddress.Equals(_serverIdentifierOption.IPAddress))
                {
                    result = true;
                }
                else
                {
                    Trace(string.Format("Client sent message with non-matching server identifier '{0}' -> ignored", dhcpOptionServerIdentifier.IPAddress));
                }
            }
            else
            {
                Trace("Client sent message without filling required ServerIdentifier option -> ignored");
            }
            return result;
        }

        private void OnReceiveRequest(DHCPMessage dhcpMessage)
        {
            lock (_leasesSync)
            {
                // is it a known client?
                var lease = LeasesManager.Get(dhcpMessage.ClientHardwareAddress);

                // is there a server identifier?
                var dhcpOptionServerIdentifier = (DHCPOptionServerIdentifier) dhcpMessage.GetOption(TDHCPOption.ServerIdentifier);
                var dhcpOptionRequestedIPAddress = (DHCPOptionRequestedIPAddress) dhcpMessage.GetOption(TDHCPOption.RequestedIPAddress);

                if (dhcpOptionServerIdentifier != null)
                {
                    // there is a server identifier: the message is in response to a DHCPOFFER
                    if (dhcpOptionServerIdentifier.IPAddress.Equals(_serverIdentifierOption.IPAddress))
                    {         
                        // it's a response to OUR offer.
                        // but did we actually offer one?
                        if (lease != null && lease.Status == DHCPLeaseStatus.Offered)
                        {
                            // yes.
                            // the requested IP address MUST be filled in with the offered address
                            if(dhcpOptionRequestedIPAddress != null)
                            {
                                if(lease.Address.Equals(dhcpOptionRequestedIPAddress.IPAddress))
                                {
                                    Trace("Client request matches offered address -> ACK");
                                    lease.Status = DHCPLeaseStatus.Bounded;
                                    LeasesManager.Update(lease);
                                    SendACK(dhcpMessage, lease);
                                }
                                else
                                {
                                    Trace(
                                        string.Format(
                                            "Client sent request for IP address '{0}', but it does not match the offered address '{1}' -> NAK",
                                            dhcpOptionRequestedIPAddress.IPAddress,
                                            lease.Address));
                                    SendNAK(dhcpMessage);
                                    LeasesManager.Remove(lease);
                                }
                            }                   
                            else
                            {
                                Trace("Client sent request without filling the RequestedIPAddress option -> NAK");
                                SendNAK(dhcpMessage);
                                LeasesManager.Remove(lease);
                            }
                        }
                        else
                        {          
                            // we don't have an outstanding offer!
                            Trace("Client requested IP address from this server, but we didn't offer any. -> NAK");
                            SendNAK(dhcpMessage);
                        }
                    }
                    else
                    {
                        Trace(
                            string.Format(
                                "Client requests IP address that was offered by another DHCP server at '{0}' -> drop offer",
                                dhcpOptionServerIdentifier.IPAddress));
                        // it's a response to another DHCP server.
                        // if we sent an OFFER to this client earlier, remove it.
                        if(lease!=null)
                        {
                            LeasesManager.Remove(lease);
                        }
                    }
                }
                else
                {
                    // no server identifier: the message is a request to verify or extend an existing lease
                    // Received REQUEST without server identifier, client is INIT-REBOOT, RENEWING or REBINDING

                    Trace("Received REQUEST without server identifier, client state is INIT-REBOOT, RENEWING or REBINDING");

                    if (!dhcpMessage.ClientIPAddress.Equals(IPAddress.Any))
                    {
                        Trace("REQUEST client IP is filled in -> client state is RENEWING or REBINDING");
                        if (lease != null)
                        {
                            if (lease.Address != null && lease.Address.Equals(dhcpMessage.ClientIPAddress))
                            {
                                // All leases with this mac and this ip that we use in past, we will be use now.
                                lease.Status = DHCPLeaseStatus.Bounded;
                                LeasesManager.Update(lease);
                                SendACK(dhcpMessage, lease);
                            }
                            else
                            {
                                if (!lease.Static)
                                {
                                    Trace("Lease IP in Leases incorrect, try to re-init lease");
                                    // Founded lease have another allocated ip
                                    // Free Allocated IP and try to allocate IP from request.
                                    LeasesManager.Remove(lease);
                                    
                                    var allocatedIP = LeasesManager.Pool.AllocateIPAddress(dhcpMessage.ClientIPAddress);
                                    if (!allocatedIP.Equals(IPAddress.Any))
                                    {
                                        var newLease = LeasesManager.Create(dhcpMessage.ClientHardwareAddress);
                                        newLease.UpdateFromMessage(dhcpMessage);
                                        newLease.Status = DHCPLeaseStatus.Bounded;
                                        newLease.Address = allocatedIP;
                                        LeasesManager.Update(newLease);
                                        SendACK(dhcpMessage, newLease);
                                    }
                                    else
                                    {
                                        Trace("Renewing client IP address already in use. Oops..");
                                    }
                                }
                                else
                                {
                                    SendNAK(dhcpMessage);
                                }
                            }
                        }
                        else
                        {
                            var allocatedIP = LeasesManager.Pool.AllocateIPAddress(dhcpMessage.ClientIPAddress);
                            if (!allocatedIP.Equals(IPAddress.Any))
                            {
                                var newLease = LeasesManager.Create(dhcpMessage.ClientHardwareAddress);
                                newLease.UpdateFromMessage(dhcpMessage);
                                newLease.Status = DHCPLeaseStatus.Bounded;
                                LeasesManager.Update(newLease);
                                SendOFFER(dhcpMessage, newLease);
                            }
                            else
                                SendNAK(dhcpMessage);
                        }
                        
                    }
                    else
                    {
                        Trace("REQUEST client IP is empty -> client state is INIT-REBOOT");

                        if (dhcpOptionRequestedIPAddress != null)
                        {
                            if (lease != null &&
                                lease.Status == DHCPLeaseStatus.Bounded)
                            {
                                if (lease.Address.Equals(dhcpOptionRequestedIPAddress.IPAddress))
                                {
                                    Trace("Client request matches cached address -> ACK");
                                    // known, assigned, and IP address matches administration. ACK
                                    lease.Status = DHCPLeaseStatus.Bounded;
                                    LeasesManager.Update(lease);
                                    SendACK(dhcpMessage, lease);
                                }
                                else
                                {
                                    Trace(string.Format("Client sent request for IP address '{0}', but it does not match cached address '{1}' -> NAK", dhcpOptionRequestedIPAddress.IPAddress, lease.Address));
                                    SendNAK(dhcpMessage);
                                    LeasesManager.Remove(lease);
                                }
                            }
                            else
                            {
                                // client not known, or known but in some other state.
                                // send NAK so client will drop to INIT state where it can acquire a new lease.
                                // see also: http://tcpipguide.com/free/t_DHCPGeneralOperationandClientFiniteStateMachine.htm
                                Trace("Client attempted INIT-REBOOT REQUEST but server has no lease for this client -> NAK");
                                SendNAK(dhcpMessage);
                                if (lease != null)
                                {
                                    LeasesManager.Remove(lease);
                                }
                            }
                        }
                        else
                        {
                            Trace("Client sent apparent INIT-REBOOT REQUEST but with an empty 'RequestedIPAddress' option. Oops..");
                        }
                    }
                }
            }
        }
        private void OnReceiveDiscover(DHCPMessage dhcpMessage)
        {
            lock(_leasesSync)
            {
                // is it a known client?
                var lease = LeasesManager.Get(dhcpMessage.ClientHardwareAddress);
                
                if(lease != null)
                {
                    Trace(string.Format("Client is known, in state {0}",lease.Status));

                    if (lease.Status == DHCPLeaseStatus.Offered || lease.Status == DHCPLeaseStatus.Bounded)
                    {
                        Trace("Client sent DISCOVER but we already offered, or assigned -> repeat offer with known address");
                    }
                    else
                    {
                        Trace("Client is known but released, use old ip address");
                    }

                    lease.Status = DHCPLeaseStatus.Offered;
                    LeasesManager.Update(lease);
                    SendOFFER(dhcpMessage, lease);
                }
                else
                {
                    Trace("Client is not known yet");
                    // client is not known yet.
                    // allocate new address, add client to client table in Offered state
                    var ipAddress = LeasesManager.Pool.AllocateIPAddress();
                    // allocation ok ?
                    if (!ipAddress.Equals(IPAddress.Any))
                    {
                        lease = LeasesManager.Create(dhcpMessage.ClientHardwareAddress);
                        lease.UpdateFromMessage(dhcpMessage);
                        lease.Address = ipAddress;
                        lease.Status = DHCPLeaseStatus.Offered;
                        LeasesManager.Update(lease);
                        SendOFFER(dhcpMessage, lease);
                    }
                    else
                        Trace("No more free addresses. Don't respond to discover");
                }
            }
        }

        private void OnReceiveDecline(DHCPMessage dhcpMessage)
        {
            lock(_leasesSync)
            {
                if (ServerIdentifierPrecondition(dhcpMessage))
                {
                    // is it a known client?
                    var lease = LeasesManager.Get(dhcpMessage.ClientHardwareAddress);

                    if (lease != null)
                    {
                        Trace("Found client in client table, removing.");
                        LeasesManager.Remove(lease);
                        
                        // the network address that should be marked as not available MUST be 
                        // specified in the RequestedIPAddress option.                                        
                        DHCPOptionRequestedIPAddress dhcpOptionRequestedIPAddress = (DHCPOptionRequestedIPAddress)dhcpMessage.GetOption(TDHCPOption.RequestedIPAddress);
                        if(dhcpOptionRequestedIPAddress!=null)
                        {
                            if(dhcpOptionRequestedIPAddress.IPAddress.Equals(lease.Address))
                            {
                                LeasesManager.Pool.MarkAsUnused(lease.Address);
                            }
                        }
                    }
                    else
                    {
                        Trace("Client not found in client table -> decline ignored.");                                        
                    }
                }
            }
        }
        
        private void OnReceive(UDPSocket sender, IPEndPoint endPoint, ArraySegment<byte> data)
        {
            try
            {
                Trace("Incoming packet - parsing DHCP Message");

                // translate array segment into a DHCPMessage
                DHCPMessage dhcpMessage = DHCPMessage.FromStream(new MemoryStream(data.Array, data.Offset, data.Count, false, false));
                Trace(Utils.PrefixLines(dhcpMessage.ToString(),"c->s "));

                // only react to messages from client to server. Ignore other types.
                if (dhcpMessage.Opcode == DHCPMessage.TOpcode.BootRequest)
                {
                    Trace(string.Format("Client {0} sent {1}", Utils.BytesToHexString(dhcpMessage.ClientHardwareAddress, "-"), dhcpMessage.MessageType));

                    switch (dhcpMessage.MessageType)
                    {
                        // DHCPDISCOVER - client to server
                        // broadcast to locate available servers
                        case TDHCPMessageType.DISCOVER:
                            OnReceiveDiscover(dhcpMessage);
                            break;

                        // DHCPREQUEST - client to server
                        // Client message to servers either 
                        // (a) requesting offered parameters from one server and implicitly declining offers from all others.
                        // (b) confirming correctness of previously allocated address after e.g. system reboot, or
                        // (c) extending the lease on a particular network address
                        case TDHCPMessageType.REQUEST:
                            OnReceiveRequest(dhcpMessage);
                            break;
                        // If the server receives a DHCPDECLINE message, the client has
                        // discovered through some other means that the suggested network
                        // address is already in use.  The server MUST mark the network address
                        // as not available and SHOULD notify the local system administrator of
                        // a possible configuration problem.
                        case TDHCPMessageType.DECLINE:
                            OnReceiveDecline(dhcpMessage);
                            break;

                        case TDHCPMessageType.RELEASE:
                            // relinguishing network address and cancelling remaining lease.
                            // Upon receipt of a DHCPRELEASE message, the server marks the network
                            // address as not allocated.  The server SHOULD retain a record of the
                            // client's initialization parameters for possible reuse in response to
                            // subsequent requests from the client.
                            lock(_leasesSync)
                            {
                                if (ServerIdentifierPrecondition(dhcpMessage))
                                {
                                    // is it a known client?
                                    var lease = LeasesManager.Get(dhcpMessage.ClientHardwareAddress);

                                    if (lease != null /* && knownClient.State == DHCPClient.TState.Assigned */ )
                                    {
                                        if (dhcpMessage.ClientIPAddress.Equals(lease.Address))
                                        {
                                            Trace("Found client in client table, marking as released");
                                            lease.Status = DHCPLeaseStatus.Released;
                                            LeasesManager.Update(lease);
                                        }
                                        else
                                        {
                                            Trace("IP address in RELEASE doesn't match known client address. Mark this client as released with unknown IP");
                                            LeasesManager.Remove(lease);
                                        }
                                    }
                                    else
                                    {
                                        Trace("Client not found in client table, release ignored.");
                                    }
                                }
                            }
                            break;

                        // DHCPINFORM - client to server
                        // client asking for local configuration parameters, client already has externally configured
                        // network address.
                        case TDHCPMessageType.INFORM:
                            // The server responds to a DHCPINFORM message by sending a DHCPACK
                            // message directly to the address given in the 'ciaddr' field of the
                            // DHCPINFORM message.  The server MUST NOT send a lease expiration time
                            // to the client and SHOULD NOT fill in 'yiaddr'.  The server includes
                            // other parameters in the DHCPACK message as defined in section 4.3.1.
                            SendINFORMACK(dhcpMessage);
                            break;

                        default:
                            Trace(string.Format("Invalid message from client, ignored"));
                            break;
                    }

                    HandleStatusChange(null);
                }
            }
            catch(Exception e)
            {   
                System.Diagnostics.Debug.WriteLine(e.Message);
                System.Diagnostics.Debug.WriteLine(e.StackTrace);
            }
        }

        private void OnStop(UDPSocket sender, Exception reason)
        {
            Stop(reason);
        }
    }
}
