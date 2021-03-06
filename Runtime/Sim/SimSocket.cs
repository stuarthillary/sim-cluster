﻿using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimMach.Sim {
    public sealed class SimSocket : ISocket {
        readonly SimProc _proc;
        public readonly SimEndpoint Endpoint;
        readonly SimCluster _net;
        


        public readonly Dictionary<SimEndpoint, SimConn> _connections =
            new Dictionary<SimEndpoint, SimConn>(SimEndpoint.Comparer);

        readonly Queue<IConn> _incoming = new Queue<IConn>();

        SimFuture<IConn> _poll;

        public SimSocket(SimProc proc, SimEndpoint endpoint, SimCluster net) {
            _proc = proc;
            Endpoint = endpoint;
            _net = net;
        }

        public void Debug(string message) {
            _proc.Debug(message);
        }

        public Task<IConn> Accept() {
            if (_incoming.TryDequeue(out var conn)) {
                return Task.FromResult<IConn>(conn);
            }

            if (_poll != null) {
                throw new IOException("There is a wait already");
            }

            _poll = _proc.Promise<IConn>(Timeout.InfiniteTimeSpan, _proc.Token);
            return _poll.Task;
        }

        public void Deliver(SimPacket msg) {
            
            if (_connections.TryGetValue(msg.Source, out var conn)) {
                conn.Deliver(msg);
                return;
            }

            if (msg.Flag == SimFlag.Syn) {
                AcceptNewConnection(msg);    
                return;
            }
            
            Debug($"Drop non SYN: {msg.BodyString()}");
        }

        void AcceptNewConnection(SimPacket msg) {
            var conn = new SimConn(this, msg.Source, _proc, 0, msg.NextSeqNumber);
            _connections.Add(msg.Source, conn);

            _proc.Schedule(async () => {
                await conn.Write(null, SimFlag.Ack | SimFlag.Syn);
                var resp = await conn.Read(5.Sec());
                if (resp.Flag != SimFlag.Ack) {
                    Debug($"Non ACK packet: {msg.BodyString()}");
                    await conn.Write(null, SimFlag.Reset);
                    _connections.Remove(msg.Source);
                    return;
                }

                AddEstablishedConnection(conn);
            });
        }

        void AddEstablishedConnection(SimConn conn) {
            var ready = new ClientConn(conn);

            if (_poll != null) {
                _poll.SetResult(ready);
                _poll = null;
            } else {
                _incoming.Enqueue(ready);
            }
        }


        public void SendMessage(SimPacket message) {
            _net.SendPacket(message);
        }

        public void Dispose() {
            _proc.ReleaseSocket(this.Endpoint.Port);
        }
    }
}