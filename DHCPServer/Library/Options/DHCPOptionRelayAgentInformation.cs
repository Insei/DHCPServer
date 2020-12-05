/*

Copyright (c) 2020 Jean-Paul Mikkers

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
using System.IO;

namespace GitHub.JPMikkers.DHCP
{
    public class DHCPOptionRelayAgentInformation : DHCPOptionBase
    {
        // suboptions found here: http://networksorcery.com/enp/protocol/bootp/option082.htm
        private enum SubOption : byte
        {
            AgentCircuitId = 1,                         // RFC 3046
            AgentRemoteId = 2,                          // RFC 3046
            DOCSISDeviceClass = 4,                      // RFC 3256
            LinkSelection = 5,                          // RFC 3527
            SubscriberId = 6,                           // RFC 3993
            RadiusAttributes = 7,                       // RFC 4014
            Authentication = 8,                         // RFC 4030
            VendorSpecificInformation = 9,              // RFC 4243
            RelayAgentFlags = 10,                       // RFC 5010
            ServerIdentifierOverride = 11,              // RFC 5107
            DHCPv4VirtualSubnetSelection = 151,         // RFC 6607
            DHCPv4VirtualSubnetSelectionControl = 152,  // RFC 6607
        }

        private byte[] m_Data;
        private byte[] m_AgentCircuitId;
        private byte[] m_AgentRemoteId;

        public byte[] AgentCircuitId
        {
            get { return m_AgentCircuitId;  }
        }

        public byte[] AgentRemoteId
        {
            get { return m_AgentRemoteId; }
        }

        #region IDHCPOption Members

        public override IDHCPOption FromStream(Stream s)
        {
            var result = new DHCPOptionRelayAgentInformation();
            result.m_Data = new byte[s.Length];
            s.Read(result.m_Data, 0, result.m_Data.Length);

            // subOptionStream
            var suStream = new MemoryStream(m_Data);

            while (true)
            {
                int suCode = suStream.ReadByte();
                if (suCode == -1 || suCode == 255) break;
                else if (suCode == 0) continue;
                else
                {
                    int suLen = suStream.ReadByte();
                    if (suLen == -1) break;

                    switch((SubOption)suCode)
                    {
                        case SubOption.AgentCircuitId:
                            result.m_AgentCircuitId = new byte[suLen];
                            suStream.Read(result.m_AgentCircuitId, 0, suLen);
                            break;

                        case SubOption.AgentRemoteId:
                            result.m_AgentRemoteId = new byte[suLen];
                            suStream.Read(result.m_AgentRemoteId, 0, suLen);
                            break;

                        default:
                            suStream.Seek(suLen, SeekOrigin.Current);
                            break;
                    }
                }
            }

            return result;
        }

        public override void ToStream(Stream s)
        {
            s.Write(m_Data, 0, m_Data.Length);
        }

        #endregion

        public DHCPOptionRelayAgentInformation()
            : base(TDHCPOption.RelayAgentInformation)
        {
            m_Data = new byte[0];
            m_AgentCircuitId = new byte[0];
            m_AgentRemoteId = new byte[0];
        }

        public override string ToString()
        {
            return $"Option(name=[{OptionType}], value=[AgentCircuitId=[{Utils.BytesToHexString(m_AgentCircuitId," ")}], AgentRemoteId=[{Utils.BytesToHexString(m_AgentCircuitId, " ")}]])";
        }
    }
}
