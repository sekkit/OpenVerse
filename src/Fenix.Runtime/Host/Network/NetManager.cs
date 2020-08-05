﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.KCP;
using DotNetty.Transport.Channels;
using Fenix.Common;
using Fenix.Common.Utils;
using Fenix.Config;

namespace Fenix
{
    public class NetManager
    { 
        protected ConcurrentDictionary<uint, NetPeer> tcpPeers = new ConcurrentDictionary<uint, NetPeer>();

        protected ConcurrentDictionary<uint, NetPeer> kcpPeers = new ConcurrentDictionary<uint, NetPeer>();

        protected ConcurrentDictionary<uint, NetPeer> channelPeers = new ConcurrentDictionary<uint, NetPeer>();

        public ConcurrentDictionary<ulong, byte[][]> PartialRpcDic = new ConcurrentDictionary<ulong, byte[][]>();

        protected ConcurrentDictionary<ulong, long> partialRpcTimeDic = new ConcurrentDictionary<ulong, long>();

        public event Action<NetPeer> OnConnect;
        public event Action<NetPeer, IByteBuffer> OnReceive;
        public event Action<NetPeer> OnClose;
        public event Action<NetPeer, Exception> OnException;
        public event Action<NetPeer, IByteBuffer> OnSend;
        public event Action<NetPeer> OnPeerLost;

        public void RegisterChannel(IChannel channel)
        { 
            var cid = channel.Id.AsLongText();
            var id = Basic.GenID32FromName(cid);
            var peer = NetPeer.Create(id, channel);
            channelPeers[id] = peer; 
        }

        public void DeregisterChannel(IChannel ch)
        { 
            var peer = GetPeer(ch);   
            this.Deregister(peer); 
        }

        public void ChangePeerId(uint oldHostId, uint newHostId, string hostName, string address)
        {
            Log.Info(string.Format("ChangePeer: {0}=>{1} {2} {3}", oldHostId, newHostId, hostName, address));
            if (tcpPeers.ContainsKey(oldHostId))
            {
                var peer = tcpPeers[oldHostId];
                peer.ConnId = newHostId;
                tcpPeers.TryRemove(oldHostId, out var _);
                tcpPeers[newHostId] = peer;
                var hostInfo = Global.IdManager.GetHostInfo(oldHostId);
                
                //Global.IdManager.RemoveHostId(oldHostId);
                //Global.IdManager.RegisterHost(newHostId, hostName, address);
                hostInfo.HostId = newHostId;
                hostInfo.HostName = hostName;
                hostInfo.HostAddr = address;
                //Global.IdManager.RegisterHostInfo(hostInfo); 
            }
            if (kcpPeers.ContainsKey(oldHostId))
            {
                var peer = kcpPeers[oldHostId];
                peer.ConnId = newHostId;
                kcpPeers.TryRemove(oldHostId, out var _);
                kcpPeers[newHostId] = peer;
                var hostInfo = Global.IdManager.GetHostInfo(oldHostId);
                //Global.IdManager.RemoveHostId(oldHostId);
                //Global.IdManager.RegisterHost(newHostId, hostName, address);
                hostInfo.HostId = newHostId;
                hostInfo.HostName = hostName;
                hostInfo.HostAddr = address;
                //Global.IdManager.RegisterHostInfo(hostInfo); 
            } 
            if (channelPeers.ContainsKey(oldHostId))
            {
                var peer = channelPeers[oldHostId];
                peer.ConnId = newHostId; 
                if (peer.netType == NetworkType.KCP)
                    kcpPeers[newHostId] = peer;
                else
                    tcpPeers[newHostId] = peer;
                 
                //Global.IdManager.RegisterHost(newHostId, hostName, address); 
            }
        } 

        //kcp目前不支持epoll/kqueue/IOCP，所以只在客户端上用用
        public void RegisterKcp(Ukcp ukcp)
        {  
            var id = ukcp.GetUniqueId();
            var peer = NetPeer.Create(id, ukcp);
            channelPeers[peer.ConnId] = peer;
            Log.Info(string.Format("Incoming KCP id: {0}", id));
        }  

        public void DeregisterKcp(Ukcp ukcp)
        { 
            var peer = GetPeer(ukcp);
            this.Deregister(peer);
        }

        public void Deregister(NetPeer peer)
        {
            if (peer == null)
                return; 
            
            peer.Stop();

            peer.OnClose -= OnClose;
            peer.OnReceive -= OnReceive;
            peer.OnException -= OnException;

            if (peer.netType == NetworkType.KCP)  
                kcpPeers.TryRemove(peer.ConnId, out var _);  

            if (peer.netType == NetworkType.TCP) 
                tcpPeers.TryRemove(peer.ConnId, out var _); 

            if (channelPeers.ContainsKey(peer.ConnId))
                channelPeers.TryRemove(peer.ConnId, out var _);

            Log.Info(string.Format("DeregisterPeer: {0} {1} {2}", peer.ConnId, peer.RemoteAddress, peer.netType));
        }

#if !CLIENT
        public void RegisterClient(uint clientId, string uniqueName, NetPeer peer)
        {
            var addr = peer.RemoteAddress.ToIPv4String();
            if (peer.netType == NetworkType.KCP)
                kcpPeers[clientId] = peer;
            else if (peer.netType == NetworkType.TCP)
                tcpPeers[clientId] = peer; 
            Global.IdManager.RegisterClientHost(clientId, uniqueName, peer.RemoteAddress.ToIPv4String()); 
        }
#endif

        public void RemovePeerId(uint connId)
        {
            tcpPeers.TryRemove(connId, out var _);
            kcpPeers.TryRemove(connId, out var _);
            Global.IdManager.RemoveHostId(connId);
        }

        public NetPeer GetPeer(IChannel ch)
        {
            var cid = ch.Id.AsLongText();
            var id = Basic.GenID32FromName(cid);
            return channelPeers[id];
        }

        public NetPeer GetPeer(Ukcp ukcp)
        {
            var id = ukcp.GetUniqueId();
            channelPeers.TryGetValue(id, out var peer);
            return peer;
        }

        public NetPeer GetPeerById(uint peerId, NetworkType netType)
        {
            NetPeer peer;
            if (netType == NetworkType.TCP)
            {
                if (tcpPeers.TryGetValue(peerId, out peer))
                    return peer;
            }
            else
            {
                if (kcpPeers.TryGetValue(peerId, out peer))
                    return peer;
            }

            if (channelPeers.ContainsKey(peerId))
                channelPeers.TryGetValue(peerId, out peer);

            return peer;
        }

        public NetPeer CreatePeer(uint remoteHostId, IPEndPoint addr, NetworkType netType)
        { 
            var peer = GetPeerById(remoteHostId, netType);
            if (peer != null)
                return peer;
            peer = NetPeer.Create(remoteHostId, addr, netType);
            if (peer == null)
                return null;
            peer.OnClose += this.OnClose;
            peer.OnReceive += OnReceive;
            peer.OnException += OnException;
            if (netType == NetworkType.TCP)
                tcpPeers[peer.ConnId] = peer;
            else
                kcpPeers[peer.ConnId] = peer;
            peer.Register();
            return peer; 
        }

        //peer connects to hosts(interprocesses)
        public NetPeer CreatePeer(string ip, int port, NetworkType netType)
        {
//#if !CLIENT
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            var addr = ep.ToString();

            var hostId = Global.IdManager.GetHostId(addr);
            if (hostId != 0)
                return Global.NetManager.GetPeerById(hostId, netType);
//#endif

            var peer = NetPeer.Create(ep, netType);
            if (peer == null)
                return null;

            peer.OnClose += this.OnClose;
            peer.OnReceive += OnReceive;
            peer.OnException += OnException; 

            if (netType == NetworkType.TCP)
                tcpPeers[peer.ConnId] = peer;
            else
                kcpPeers[peer.ConnId] = peer;
            peer.Register();
            return peer;
        }

        //服务端都是tcp，所以不需要心跳？暂时都是发ping/pong包，5s一次，5次没收到就断开 
        public void Ping(bool pingServerOnly)
        {
            foreach (var p in tcpPeers.Values)
                if (pingServerOnly && !p.IsRemoteClient)
                    p?.Ping();
                else if (!pingServerOnly && p.IsRemoteClient)
                    p?.Ping();
            //p?.Ping();

            foreach (var p in kcpPeers.Values)
                if (pingServerOnly && !p.IsRemoteClient)
                    p?.Ping();
                else if (!pingServerOnly && p.IsRemoteClient)
                    p?.Ping();
            //p?.Ping();
        }

        public void OnPong(NetPeer peer)
        {
            peer.lastTickTime = TimeUtil.GetTimeStampMS();
            Log.Info(string.Format("PONG({0}) {1} from {2}", peer.netType, peer.ConnId, peer.RemoteAddress?.ToString()));
        }

        public void Send(NetPeer peer, Packet packet)
        {
            var bytes = packet.Pack();

            if (bytes.Length > Global.Config.MAX_PACKET_SIZE)
            {
                PartialSendAsync(peer, bytes);
                return;
            }

            peer.Send(bytes);
        }

        protected async Task PartialSendAsync(NetPeer peer, byte[] bytes)
        {
            var parts = DataUtil.SplitBytes(bytes, Global.Config.MAX_PACKET_SIZE);
            var partialId = Basic.GenID64();
            var totalPartNum = parts.Count();
            if(totalPartNum > 256)
            {
                Log.Error("send_bytes_too_long", peer.ConnId, totalPartNum);
                return;
            }
            for (short i = 0; i < parts.Count(); ++i)
            {
                var part = parts.ElementAt(i);
                var partialBuf = Unpooled.DirectBuffer();
                partialBuf.WriteIntLE((int)OpCode.PARTIAL);
                partialBuf.WriteLongLE((long)partialId);
                partialBuf.WriteByte(i);
                partialBuf.WriteByte(parts.Count());
                partialBuf.WriteBytes(part);
                peer.Send(partialBuf);
                await Task.Delay(10);
                Log.Info("send_part", i, parts.Count(), part.Length);
            }
        }

        public byte[] AddPartialRpc(ulong partialId, int partIndex, int totPartCount, byte[] payload)
        {
            if (!PartialRpcDic.ContainsKey(partialId))
                PartialRpcDic[partialId] = new byte[totPartCount][];
            PartialRpcDic[partialId][partIndex] = payload;
            partialRpcTimeDic[partialId] = TimeUtil.GetTimeStampMS();
            Log.Info("recv_part", partIndex, totPartCount, payload.Length);
            if (PartialRpcDic[partialId].Count(m => m != null) == totPartCount)
            {
                byte[] finalBytes = DataUtil.ConcatBytes(PartialRpcDic[partialId]);
                PartialRpcDic.TryRemove(partialId, out var _);
                partialRpcTimeDic.TryRemove(partialId, out var _);
                return finalBytes;
            }

            return null;
        }

        long lastTick = 0;

        public void Update()
        {
            var curTime = TimeUtil.GetTimeStampMS();

            if (curTime - lastTick < 5000)
                return;

            lastTick = curTime;

            CheckPeers(tcpPeers.Values);
            CheckPeers(kcpPeers.Values);
            
            foreach (var partialId in partialRpcTimeDic.Keys.ToArray())
            {
                var ts = partialRpcTimeDic[partialId];
                if (curTime - ts > 15000)
                {
                    Log.Info("CheckPartialRpc->timeout");
                    PartialRpcDic.TryRemove(partialId, out var _);
                    partialRpcTimeDic.TryRemove(partialId, out var _);
                }
            }
        }

        void CheckPeers(ICollection<NetPeer> peers)
        {
            var curTS = TimeUtil.GetTimeStampMS(); 
            foreach (var p in peers)
            {
                if (p.IsActive == false)
                {
                    Log.Info(string.Format("Remove: {0} {1} {2}", p.ConnId, p.RemoteAddress, p.netType));
                    this.Deregister(p);
                    continue;
                }
#if CLIENT
                if (curTS - p.lastTickTime >= Global.Config.HeartbeatIntervalMS * 3)
                {
                    this.PrintPeerInfo("SEKKIT");
                    Log.Info(string.Format("Timeout: {0} {1} {2}", p.ConnId, p.RemoteAddress, p.netType));
                    this.Deregister(p);
                }
#endif
            }
        }

        public void PrintPeerInfo(string header="")
        {
            if (header != "")
                Log.Info(header);

            foreach (var p in tcpPeers.Values)
            {
                Log.Info(string.Format("========Peer({0}): {1} {2} {3} active:{4}", p.netType, p.ConnId, p.RemoteAddress, p.LocalAddress, p.IsActive)); 
            }

            foreach (var p in kcpPeers.Values)
            {
                Log.Info(string.Format("========Peer({0}): {1} {2} {3} active:{4}", p.netType, p.ConnId, p.RemoteAddress, p.LocalAddress, p.IsActive));
            } 
        }

        public void Destroy()
        {
            foreach (var p in tcpPeers.Values)
                Deregister(p);
            tcpPeers.Clear();
            foreach (var p in kcpPeers.Values)
                Deregister(p);
            kcpPeers.Clear();
            foreach (var p in channelPeers.Values)
                Deregister(p);
            channelPeers.Clear();

            this.OnClose = null;
            this.OnConnect = null;
            this.OnException = null;
            this.OnReceive = null;
            this.OnSend = null;
            this.OnPeerLost = null;
            Global.NetManager = null;
        }
    }
}
