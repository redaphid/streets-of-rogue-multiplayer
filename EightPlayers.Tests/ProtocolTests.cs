using EightPlayers.EcsNet;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EightPlayers.Tests
{
    // The C# protocol must stay wire-compatible with worker/src/protocol.ts.
    // These tests pin the exact JSON shapes both sides agreed on; if one fails
    // after editing either twin, the twins have drifted.
    public class ProtocolTests
    {
        [Fact]
        public void HelloShape()
        {
            var jo = JObject.Parse(Protocol.Hello("alice"));
            Assert.Equal("hello", (string)jo["t"]);
            Assert.Equal(Protocol.Version, (int)jo["proto"]);
            Assert.Equal("alice", (string)jo["name"]);
        }

        [Fact]
        public void SpawnShape()
        {
            var jo = JObject.Parse(Protocol.Spawn(7, Protocol.PlayerComponents("bob", 2, "Thief", 1.5f, -3f, 90f, 100f)));
            Assert.Equal("spawn", (string)jo["t"]);
            Assert.Equal(7, (int)jo["tmp"]);
            Assert.Equal("bob", (string)jo["components"]["player"]["name"]);
            Assert.Equal(2, (int)jo["components"]["player"]["color"]);
            Assert.Equal("Thief", (string)jo["components"]["player"]["char"]);
            Assert.Equal(1.5f, (float)jo["components"]["pos"]["x"]);
            Assert.Equal(-3f, (float)jo["components"]["pos"]["y"]);
            Assert.Equal(90f, (float)jo["components"]["hp"]["cur"]);
            Assert.Equal(100f, (float)jo["components"]["hp"]["max"]);
        }

        [Fact]
        public void HpComponentShape()
        {
            var jo = Protocol.HpComponent(33f, 50f);
            Assert.Equal(33f, (float)jo["hp"]["cur"]);
            Assert.Equal(50f, (float)jo["hp"]["max"]);
        }

        [Fact]
        public void LevelComponentShape()
        {
            var jo = Protocol.LevelComponent(-123456, 3);
            Assert.Equal(-123456, (int)jo["level"]["seed"]);
            Assert.Equal(3, (int)jo["level"]["num"]);
        }

        [Fact]
        public void LevelMergesIntoPlayerComponents()
        {
            var components = Protocol.PlayerComponents("a", 1, "Hobo", 0f, 0f, 100f, 100f);
            components.Merge(Protocol.LevelComponent(7, 1));
            Assert.Equal(7, (int)components["level"]["seed"]);
            Assert.Equal("a", (string)components["player"]["name"]);
        }

        [Fact]
        public void SetShape()
        {
            var jo = JObject.Parse(Protocol.Set(12, Protocol.PosComponent(4f, 5f)));
            Assert.Equal("set", (string)jo["t"]);
            Assert.Equal(12, (int)jo["e"]);
            Assert.Equal(4f, (float)jo["components"]["pos"]["x"]);
        }

        [Fact]
        public void DespawnShape()
        {
            var jo = JObject.Parse(Protocol.Despawn(9));
            Assert.Equal("despawn", (string)jo["t"]);
            Assert.Equal(9, (int)jo["e"]);
        }

        [Fact]
        public void ParsesWelcome()
        {
            var msg = ServerMsg.Parse(
                "{\"t\":\"welcome\",\"proto\":1,\"you\":3,\"room\":\"AB-1\",\"peers\":[{\"id\":1,\"name\":\"x\"}]," +
                "\"snapshot\":[{\"e\":2,\"owner\":1,\"components\":{\"pos\":{\"x\":1,\"y\":2}}}]}");
            Assert.Equal("welcome", msg.T);
            Assert.Equal(3, msg.You);
            Assert.Equal("AB-1", msg.Room);
            Assert.Single(msg.Peers);
            Assert.Single(msg.Snapshot);
            Assert.Equal(2, (int)msg.Snapshot[0]["e"]);
        }

        [Fact]
        public void ParsesSpawnWithAndWithoutTmp()
        {
            var mine = ServerMsg.Parse("{\"t\":\"spawn\",\"e\":4,\"owner\":2,\"components\":{},\"tmp\":11}");
            Assert.Equal(11, mine.Tmp);
            Assert.Equal(4, mine.Entity);
            Assert.Equal(2, mine.Owner);

            var theirs = ServerMsg.Parse("{\"t\":\"spawn\",\"e\":5,\"owner\":3,\"components\":{}}");
            Assert.Equal(-1, theirs.Tmp);
        }

        [Fact]
        public void ParsesSetDespawnPeerError()
        {
            var set = ServerMsg.Parse("{\"t\":\"set\",\"e\":8,\"components\":{\"pos\":{\"x\":0,\"y\":0}}}");
            Assert.Equal(8, set.Entity);
            Assert.NotNull(set.Components);

            var despawn = ServerMsg.Parse("{\"t\":\"despawn\",\"e\":8}");
            Assert.Equal(8, despawn.Entity);

            var peer = ServerMsg.Parse("{\"t\":\"peer\",\"id\":6,\"name\":\"carol\",\"joined\":false}");
            Assert.Equal(6, peer.PeerId);
            Assert.Equal("carol", peer.PeerName);
            Assert.False(peer.Joined);

            var error = ServerMsg.Parse("{\"t\":\"error\",\"message\":\"nope\"}");
            Assert.Equal("nope", error.Message);
        }
    }
}
