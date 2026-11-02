using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// Playing in one world with friends. Only in modded lobbies, and only with players who also run GorillaMerge:
    /// each of us says so with a player property, and our messages go only to those players. Gorilla Tag itself
    /// ignores event codes it doesn't know, and nobody without the mod is sent anything.
    ///
    /// The world belongs to whichever of us has been in the room longest (the lowest actor number, the "owner").
    /// Everyone else gets a copy of the owner's world (seed, place in the sky, every changed chunk) when they travel
    /// there; after that every block anyone places or breaks is sent to everyone. Only the owner saves the shared
    /// world. Everyone keeps their own inventory. Mobs and dropped items stay on each player's own game.
    /// </summary>
    public class Multiplayer
    {
        private const byte Code = 191;
        private const string PropKey = "GorillaMerge";
        private const string Protocol = "1";
        private const int PartSize = 48 * 1024, MaxParts = 400, MaxEdits = 20000;

        private enum Msg : byte { WantWorld = 1, World = 2, NoWorld = 3, Edits = 4, Time = 5 }

        public class WorldData
        {
            public int Seed;
            public Vector3 Origin;
            public float BlockSize, Time;
            public byte[] Bytes;
        }

        private class Incoming
        {
            public int From, Seed, Got;
            public float[] Info;
            public byte[][] Parts;
        }

        private readonly MergeManager _mgr;
        private readonly List<int> _peers = new List<int>();
        private readonly Dictionary<string, Incoming> _incoming = new Dictionary<string, Incoming>();
        private readonly Dictionary<string, WorldData> _arrived = new Dictionary<string, WorldData>();
        private readonly HashSet<string> _refused = new HashSet<string>();
        private readonly Dictionary<string, List<(Vector3Int p, ushort id)>> _early = new Dictionary<string, List<(Vector3Int, ushort)>>();
        private readonly Dictionary<string, List<(Vector3Int p, ushort id)>> _outgoing = new Dictionary<string, List<(Vector3Int, ushort)>>();
        private readonly Queue<(int to, object[] content)> _sendQueue = new Queue<(int, object[])>();
        private float _nextCheck, _nextFlush, _retellAt;
        private string _toldRoom;
        private bool _hooked;

        /// <summary>In a modded lobby with at least one other GorillaMerge player.</summary>
        public bool Active { get; private set; }
        public int OwnerActor { get; private set; } = -1;
        public bool IsOwner => Active && OwnerActor == LocalActor;
        public int FriendCount => _peers.Count;
        public string OwnerName { get; private set; } = "";
        /// <summary>Changes whenever who owns the shared world changes ("" when not sharing).</summary>
        public string SessionKey => Active ? PhotonNetwork.CurrentRoom?.Name + "#" + OwnerActor : "";

        private static int LocalActor => PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        public Multiplayer(MergeManager mgr)
        {
            _mgr = mgr;
        }

        public void Tick()
        {
            if (!_hooked)
            {
                try { PhotonNetwork.NetworkingClient.EventReceived += OnEvent; _hooked = true; }
                catch (Exception ex) { Plugin.Log.LogWarning("Friends' worlds unavailable: " + ex.Message); _hooked = true; }
            }
            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + 1f;
                RefreshPeers();
            }
            if (!Active) return;

            if (Time.time >= _nextFlush)
            {
                _nextFlush = Time.time + 0.1f;
                foreach (var kv in _outgoing)
                {
                    var list = kv.Value;
                    for (int i = 0; i < list.Count; i += 4000)
                        Send(_peers.ToArray(), EncodeEdits(kv.Key, list.GetRange(i, Math.Min(4000, list.Count - i))));
                    list.Clear();
                }
            }
            // A big world goes out a couple of pieces per frame so it doesn't choke the connection.
            for (int n = 0; n < 2 && _sendQueue.Count > 0; n++)
            {
                var (to, content) = _sendQueue.Dequeue();
                if (_peers.Contains(to)) Send(new[] { to }, content);
            }
        }

        private void RefreshPeers()
        {
            bool was = Active;
            _peers.Clear();
            bool inRoom = false;
            try { inRoom = PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null; } catch { }
            if (inRoom && Plugin.Allowed)
            {
                // Tell other GorillaMerge players we're here (only in modded lobbies; once per room, retried slowly).
                var me = PhotonNetwork.LocalPlayer;
                string room = PhotonNetwork.CurrentRoom?.Name;
                bool told = me.CustomProperties.TryGetValue(PropKey, out var mine) && Protocol.Equals(mine);
                if (!told && (room != _toldRoom || Time.time >= _retellAt))
                {
                    me.SetCustomProperties(new Hashtable { [PropKey] = Protocol });
                    _toldRoom = room;
                    _retellAt = Time.time + 15f;
                }
                foreach (var p in PhotonNetwork.PlayerListOthers)
                    if (p.CustomProperties.TryGetValue(PropKey, out var v) && Protocol.Equals(v)) _peers.Add(p.ActorNumber);
            }
            Active = _peers.Count > 0;
            int owner = LocalActor;
            foreach (int a in _peers) if (a < owner) owner = a;
            OwnerActor = Active ? owner : -1;
            OwnerName = "";
            if (Active)
            {
                var op = PhotonNetwork.CurrentRoom?.GetPlayer(OwnerActor);
                OwnerName = op != null ? op.NickName : "";
            }
            if (!Active)
            {
                _incoming.Clear(); _arrived.Clear(); _refused.Clear(); _early.Clear(); _outgoing.Clear(); _sendQueue.Clear();
            }
            if (Active != was) Plugin.Log.LogInfo(Active ? $"Friends with GorillaMerge here: {_peers.Count}. World owner: {(IsOwner ? "you" : OwnerName)}." : "No GorillaMerge friends in this room.");
        }

        // ---------- Asking for and sending worlds ----------

        public void RequestWorld(Pack pack)
        {
            _arrived.Remove(pack.Id);
            _refused.Remove(pack.Id);
            _incoming.Remove(pack.Id);
            if (Active && !IsOwner) Send(new[] { OwnerActor }, new object[] { (byte)Msg.WantWorld, pack.Id });
        }

        public bool TryTakeWorld(string packId, out WorldData data)
        {
            if (!_arrived.TryGetValue(packId, out data)) return false;
            _arrived.Remove(packId);
            return true;
        }

        public bool WasRefused(string packId) => _refused.Contains(packId);

        /// <summary>Edits that came in while the world was still on its way; apply them after loading it.</summary>
        public List<(Vector3Int p, ushort id)> TakeEarlyEdits(string packId)
        {
            if (!_early.TryGetValue(packId, out var list)) return null;
            _early.Remove(packId);
            return list;
        }

        private void SendWorld(int to, string packId)
        {
            var w = _mgr.WorldToShare(packId);
            if (w == null) { Send(new[] { to }, new object[] { (byte)Msg.NoWorld, packId }); return; }
            var bytes = w.SaveBytes();
            int parts = Math.Max(1, (bytes.Length + PartSize - 1) / PartSize);
            if (parts > MaxParts) { Send(new[] { to }, new object[] { (byte)Msg.NoWorld, packId }); return; }
            var info = new[] { w.Origin.x, w.Origin.y, w.Origin.z, w.S, w.TimeOfDay };
            for (int i = 0; i < parts; i++)
            {
                int len = Math.Min(PartSize, bytes.Length - i * PartSize);
                var chunk = new byte[len];
                Buffer.BlockCopy(bytes, i * PartSize, chunk, 0, len);
                _sendQueue.Enqueue((to, new object[] { (byte)Msg.World, packId, w.Gen.Seed, info, i, parts, chunk }));
            }
            Plugin.Log.LogInfo($"Sending the world to a friend ({bytes.Length / 1024} KB in {parts} pieces).");
        }

        // ---------- Edits ----------

        public void QueueEdit(string packId, Vector3Int p, ushort id)
        {
            if (!Active) return;
            if (!_outgoing.TryGetValue(packId, out var list)) _outgoing[packId] = list = new List<(Vector3Int, ushort)>();
            list.Add((p, id));
        }

        public void SendTime(string packId, float time)
        {
            if (IsOwner) Send(_peers.ToArray(), new object[] { (byte)Msg.Time, packId, time });
        }

        internal static object[] EncodeEdits(string packId, List<(Vector3Int p, ushort id)> edits)
        {
            var xyz = new int[edits.Count * 3];
            var keys = new int[edits.Count];
            var palette = new List<string>();
            var lookup = new Dictionary<ushort, int>();
            for (int i = 0; i < edits.Count; i++)
            {
                var (p, id) = edits[i];
                xyz[i * 3] = p.x; xyz[i * 3 + 1] = p.y; xyz[i * 3 + 2] = p.z;
                if (!lookup.TryGetValue(id, out int k))
                {
                    k = palette.Count;
                    lookup[id] = k;
                    palette.Add(id == 0 ? "air" : BlockRegistry.All[id].Key);
                }
                keys[i] = k;
            }
            return new object[] { (byte)Msg.Edits, packId, xyz, palette.ToArray(), keys };
        }

        /// <summary>Reads an edits message from someone else's game, skipping anything that doesn't make sense.</summary>
        internal static List<(Vector3Int p, ushort id)> DecodeEdits(object[] d)
        {
            if (d.Length < 5 || !(d[2] is int[] xyz) || !(d[3] is string[] palette) || !(d[4] is int[] keys)) return null;
            if (keys.Length > MaxEdits || xyz.Length != keys.Length * 3 || palette.Length > 4096) return null;
            var ids = new int[palette.Length];
            for (int i = 0; i < palette.Length; i++)
            {
                var def = palette[i] == "air" ? null : BlockRegistry.Get(palette[i]);
                ids[i] = palette[i] == "air" ? 0 : def != null ? def.Index : -1;
            }
            var list = new List<(Vector3Int, ushort)>(keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                int k = keys[i];
                if (k < 0 || k >= ids.Length || ids[k] < 0) continue;
                var p = new Vector3Int(xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);
                if (Math.Abs(p.x) > 1_000_000 || Math.Abs(p.z) > 1_000_000 || p.y < -4096 || p.y > 4096) continue;
                list.Add((p, (ushort)ids[k]));
            }
            return list;
        }

        // ---------- Receiving ----------

        private void OnEvent(EventData ev)
        {
            if (ev.Code != Code) return;
            try { Handle(ev.Sender, ev.CustomData as object[]); }
            catch (Exception ex) { Plugin.Log.LogWarning("Ignored a bad message from a friend: " + ex.Message); }
        }

        private void Handle(int from, object[] d)
        {
            if (!Active || d == null || d.Length < 2 || !_peers.Contains(from) || !(d[0] is byte type) || !(d[1] is string packId)) return;
            switch ((Msg)type)
            {
                case Msg.WantWorld:
                    if (IsOwner) SendWorld(from, packId);
                    break;

                case Msg.NoWorld:
                    if (from == OwnerActor) _refused.Add(packId);
                    break;

                case Msg.World:
                {
                    if (from != OwnerActor || d.Length < 7 || !(d[2] is int seed) || !(d[3] is float[] info) || info.Length < 5
                        || !(d[4] is int part) || !(d[5] is int parts) || !(d[6] is byte[] bytes)) return;
                    if (parts < 1 || parts > MaxParts || part < 0 || part >= parts || bytes.Length > PartSize) return;
                    if (!_incoming.TryGetValue(packId, out var inc) || inc.From != from || inc.Parts.Length != parts || inc.Seed != seed)
                        _incoming[packId] = inc = new Incoming { From = from, Seed = seed, Info = info, Parts = new byte[parts][] };
                    if (inc.Parts[part] == null) { inc.Parts[part] = bytes; inc.Got++; }
                    if (inc.Got < parts) return;
                    int total = 0;
                    foreach (var p in inc.Parts) total += p.Length;
                    var all = new byte[total];
                    int at = 0;
                    foreach (var p in inc.Parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
                    _incoming.Remove(packId);
                    float s = Mathf.Clamp(inc.Info[3], 0.05f, 4f);
                    _arrived[packId] = new WorldData { Seed = seed, Origin = new Vector3(inc.Info[0], inc.Info[1], inc.Info[2]), BlockSize = s, Time = inc.Info[4], Bytes = all };
                    break;
                }

                case Msg.Edits:
                {
                    var edits = DecodeEdits(d);
                    if (edits == null || edits.Count == 0) return;
                    if (_mgr.ApplyFriendEdits(packId, edits)) return;
                    // Our copy of the world is still on its way: keep them for when it arrives.
                    if (_incoming.ContainsKey(packId) || _arrived.ContainsKey(packId))
                    {
                        if (!_early.TryGetValue(packId, out var list)) _early[packId] = list = new List<(Vector3Int, ushort)>();
                        if (list.Count < 200000) list.AddRange(edits);
                    }
                    break;
                }

                case Msg.Time:
                    if (from == OwnerActor && d.Length >= 3 && d[2] is float t && !float.IsNaN(t) && !float.IsInfinity(t)) _mgr.SetFriendTime(packId, t);
                    break;
            }
        }

        private static void Send(int[] to, object[] content)
        {
            if (to.Length == 0) return;
            try
            {
                PhotonNetwork.RaiseEvent(Code, content, new RaiseEventOptions { TargetActors = to }, SendOptions.SendReliable);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Couldn't send to friends: " + ex.Message); }
        }
    }
}
