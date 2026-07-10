using System.Collections.Generic;
using EightPlayers.EcsNet;
using Xunit;

namespace EightPlayers.Tests
{
    public class EcsWorldTests
    {
        [Fact]
        public void SetImplicitlySpawnsEntity()
        {
            var world = new EcsWorld();
            world.Set(5, new Pos { X = 1, Y = 2 });
            Assert.True(world.Exists(5));
            Assert.Equal(1, world.Count);
        }

        [Fact]
        public void TryGetReturnsWhatWasSet()
        {
            var world = new EcsWorld();
            world.Set(1, new Pos { X = 3.5f, Y = -2f });
            Assert.True(world.TryGet<Pos>(1, out var pos));
            Assert.Equal(3.5f, pos.X);
            Assert.Equal(-2f, pos.Y);
        }

        [Fact]
        public void TryGetMissingComponentIsFalse()
        {
            var world = new EcsWorld();
            world.Spawn(1);
            Assert.False(world.TryGet<Pos>(1, out _));
        }

        [Fact]
        public void SetOverwritesComponent()
        {
            var world = new EcsWorld();
            world.Set(1, new Pos { X = 1, Y = 1 });
            world.Set(1, new Pos { X = 9, Y = 9 });
            world.TryGet<Pos>(1, out var pos);
            Assert.Equal(9f, pos.X);
        }

        [Fact]
        public void DespawnRemovesEntityAndAllComponents()
        {
            var world = new EcsWorld();
            world.Set(1, new Pos { X = 1, Y = 1 });
            world.Set(1, new PlayerInfo { Name = "a", Color = 1 });
            world.Despawn(1);
            Assert.False(world.Exists(1));
            Assert.False(world.TryGet<Pos>(1, out _));
            Assert.False(world.TryGet<PlayerInfo>(1, out _));
            Assert.Equal(0, world.Count);
        }

        [Fact]
        public void DespawnDoesNotDisturbOtherEntities()
        {
            var world = new EcsWorld();
            world.Set(1, new Pos { X = 1, Y = 1 });
            world.Set(2, new Pos { X = 2, Y = 2 });
            world.Despawn(1);
            Assert.True(world.TryGet<Pos>(2, out var pos));
            Assert.Equal(2f, pos.X);
        }

        [Fact]
        public void ForEachVisitsOnlyEntitiesWithThatComponent()
        {
            var world = new EcsWorld();
            world.Set(1, new Pos { X = 1, Y = 1 });
            world.Set(2, new Pos { X = 2, Y = 2 });
            world.Set(3, new PlayerInfo { Name = "n", Color = 0 });
            var seen = new List<int>();
            world.ForEach<Pos>((e, _) => seen.Add(e));
            seen.Sort();
            Assert.Equal(new[] { 1, 2 }, seen);
        }

        [Fact]
        public void ClearEmptiesEverything()
        {
            var world = new EcsWorld();
            world.Set(1, new Pos());
            world.Set(2, new Owned { ClientId = 4 });
            world.Clear();
            Assert.Equal(0, world.Count);
            Assert.False(world.TryGet<Owned>(2, out _));
        }
    }
}
