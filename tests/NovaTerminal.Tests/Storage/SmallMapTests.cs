using System;
using Xunit;
using NovaTerminal.Core.Storage;

namespace NovaTerminal.Tests.Storage
{
    public class SmallMapTests
    {
        [Fact]
        public void InitialCount_IsZero()
        {
            var map = new SmallMap<string>();
            Assert.Equal(0, map.Count);
        }

        [Fact]
        public void Set_IncrementsCount()
        {
            var map = new SmallMap<string>();
            map.Set(10, "Value10");
            Assert.Equal(1, map.Count);
        }

        [Fact]
        public void TryGet_RetrievesValue()
        {
            var map = new SmallMap<string>();
            map.Set(10, "Value10");
            
            bool found = map.TryGet(10, out var value);
            
            Assert.True(found);
            Assert.Equal("Value10", value);
        }

        [Fact]
        public void TryGet_ReturnsFalse_ForMissingKey()
        {
            var map = new SmallMap<string>();
            map.Set(10, "Value10");
            
            bool found = map.TryGet(20, out var value);
            
            Assert.False(found);
            Assert.Null(value);
        }

        [Fact]
        public void Set_UpdatesExistingKey()
        {
            var map = new SmallMap<string>();
            map.Set(10, "OldValue");
            map.Set(10, "NewValue");
            
            Assert.Equal(1, map.Count);
            map.TryGet(10, out var value);
            Assert.Equal("NewValue", value);
        }

        [Fact]
        public void SmallStorage_HandlesMultipleEntries()
        {
            var map = new SmallMap<string>();
            for (int i = 0; i < 8; i++)
            {
                map.Set(i, $"Value{i}");
            }
            
            Assert.Equal(8, map.Count);
            for (int i = 0; i < 8; i++)
            {
                Assert.True(map.TryGet(i, out var value));
                Assert.Equal($"Value{i}", value);
            }
        }

        [Fact]
        public void Upgrade_ToDictionary_WhenExceedingEight()
        {
            var map = new SmallMap<string>();
            for (int i = 0; i < 9; i++)
            {
                map.Set(i, $"Value{i}");
            }
            
            Assert.Equal(9, map.Count);
            for (int i = 0; i < 9; i++)
            {
                Assert.True(map.TryGet(i, out var value));
                Assert.Equal($"Value{i}", value);
            }
        }

        [Fact]
        public void Remove_FromSmallStorage()
        {
            var map = new SmallMap<string>();
            map.Set(1, "V1");
            map.Set(2, "V2");
            map.Set(3, "V3");
            
            map.Remove(2);
            
            Assert.Equal(2, map.Count);
            Assert.True(map.TryGet(1, out _));
            Assert.False(map.TryGet(2, out _));
            Assert.True(map.TryGet(3, out _));
        }

        [Fact]
        public void Remove_FromLargeStorage()
        {
            var map = new SmallMap<string>();
            for (int i = 0; i < 10; i++)
            {
                map.Set(i, $"V{i}");
            }
            
            map.Remove(5);
            
            Assert.Equal(9, map.Count);
            Assert.False(map.TryGet(5, out _));
            Assert.True(map.TryGet(4, out _));
            Assert.True(map.TryGet(6, out _));
        }

        [Fact]
        public void Remove_NonExistentKey_DoesNothing()
        {
            var map = new SmallMap<string>();
            map.Set(1, "V1");
            map.Remove(99);
            Assert.Equal(1, map.Count);
        }
    }
}
