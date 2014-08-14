﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using Xunit;

namespace NHibernate.Caches.Redis.Tests
{
    public class RedisCacheTests : RedisTest
    {
        private readonly RedisCacheProviderOptions options = new RedisCacheProviderOptions();

        [Fact]
        void Constructor_should_set_generation_if_it_does_not_exist()
        {
            var cache = new RedisCache("regionName", ConnectionMultiplexer, options);

            var genKey = cache.CacheNamespace.GetGenerationKey();
            Assert.Contains("NHibernate-Cache:regionName", genKey);
            Assert.Equal(1, cache.CacheNamespace.GetGeneration());
        }

        [Fact]
        void Constructor_should_get_current_generation_if_it_already_exists()
        {
            // Distributed caches.
            var cache1 = new RedisCache("regionName", ConnectionMultiplexer, options);
            var cache2 = new RedisCache("regionName", ConnectionMultiplexer, options);

            Assert.Equal(1, cache1.CacheNamespace.GetGeneration());
            Assert.Equal(1, cache2.CacheNamespace.GetGeneration());
        }

        [Fact]
        void Put_should_serialize_item_and_set_with_expiry()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);

            // Act
            cache.Put(999, new Person("Foo", 10));
            // Assert
            var cacheKey = cache.CacheNamespace.GlobalCacheKey(999);
            var data = Redis.StringGet(cacheKey);
            var expiry = Redis.KeyTimeToLive(cacheKey);

            Assert.True(expiry >= TimeSpan.FromMinutes(4) && expiry <= TimeSpan.FromMinutes(5));

            var person = options.Serializer.Deserialize(data) as Person;
            Assert.NotNull(person);
            Assert.Equal("Foo", person.Name);
            Assert.Equal(10, person.Age);
        }

        [Fact]
        void Configure_region_expiration_from_config_element()
        {
            // Arrange
            var configElement = new RedisCacheElement("region", TimeSpan.FromMinutes(99));
            var props = new Dictionary<string, string>();
            var cache = new RedisCache("region", props, configElement, ConnectionMultiplexer, options);

            // Act
            cache.Put(999, new Person("Foo", 10));

            // Assert
            var cacheKey = cache.CacheNamespace.GlobalCacheKey(999);
            var expiry = Redis.KeyTimeToLive(cacheKey);
            Assert.True(expiry >= TimeSpan.FromMinutes(98) && expiry <= TimeSpan.FromMinutes(99));
        }

        [Fact]
        void Put_should_retry_until_generation_matches_the_server()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);

            // Another client incremented the generation.
            Redis.StringIncrement(cache.CacheNamespace.GetGenerationKey(), 100);

            // Act
            cache.Put(999, new Person("Foo", 10));

            // Assert
            Assert.Equal(cache.CacheNamespace.GetGeneration(), 101);
            var data = Redis.StringGet(cache.CacheNamespace.GlobalCacheKey(999));
            var person = (Person)options.Serializer.Deserialize(data);
            Assert.Equal("Foo", person.Name);
            Assert.Equal(10, person.Age);
        }

        [Fact]
        void Get_should_deserialize_data()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);
            cache.Put(999, new Person("Foo", 10));

            // Act
            var person = cache.Get(999) as Person;

            // Assert
            Assert.NotNull(person);
            Assert.Equal("Foo", person.Name);
            Assert.Equal(10, person.Age);
        }

        [Fact]
        void Get_should_return_null_if_not_exists()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);

            // Act
            var person = cache.Get(99999) as Person;

            // Assert
            Assert.Null(person);
        }

        [Fact]
        void Get_should_retry_until_generation_matches_the_server()
        {
            // Arrange
            var cache1 = new RedisCache("region", ConnectionMultiplexer, options);

            // Another client incremented the generation.
            Redis.StringIncrement(cache1.CacheNamespace.GetGenerationKey(), 100);
            var cache2 = new RedisCache("region", ConnectionMultiplexer, options);
            cache2.Put(999, new Person("Foo", 10));

            // Act
            var person = cache1.Get(999) as Person;

            // Assert
            Assert.Equal(101, cache1.CacheNamespace.GetGeneration());
            Assert.NotNull(person);
            Assert.Equal("Foo", person.Name);
            Assert.Equal(10, person.Age);
        }

        [Fact]
        void Put_and_Get_into_different_cache_regions()
        {
            // Arrange
            const int key = 1;
            var cache1 = new RedisCache("region_A", ConnectionMultiplexer, options);
            var cache2 = new RedisCache("region_B", ConnectionMultiplexer, options);

            // Act
            cache1.Put(key, new Person("A", 1));
            cache2.Put(key, new Person("B", 1));

            // Assert
            Assert.Equal("A", ((Person)cache1.Get(1)).Name);
            Assert.Equal("B", ((Person)cache2.Get(1)).Name);
        }

        [Fact]
        void Remove_should_remove_from_cache()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);
            cache.Put(999, new Person("Foo", 10));

            // Act
            cache.Remove(999);

            // Assert
            var result = Redis.StringGet(cache.CacheNamespace.GlobalCacheKey(999));
            Assert.False(result.HasValue);
        }

        [Fact]
        void Remove_should_retry_until_generation_matches_the_server()
        {
            // Arrange
            var cache1 = new RedisCache("region", ConnectionMultiplexer, options);

            // Another client incremented the generation.
            Redis.StringIncrement(cache1.CacheNamespace.GetGenerationKey(), 100);
            var cache2 = new RedisCache("region", ConnectionMultiplexer, options);
            cache2.Put(999, new Person("Foo", 10));

            // Act
            cache1.Remove(999);

            // Assert
            Assert.Equal(101, cache1.CacheNamespace.GetGeneration());
            var result = Redis.StringGet(cache1.CacheNamespace.GlobalCacheKey(999));
            Assert.False(result.HasValue);
        }

        [Fact]
        void Clear_update_generation_and_clear_keys_for_this_region()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);
            cache.Put(1, new Person("Foo", 1));
            cache.Put(2, new Person("Bar", 2));
            cache.Put(3, new Person("Baz", 3));
            var oldKey1 = cache.CacheNamespace.GlobalCacheKey(1);
            var oldKey2 = cache.CacheNamespace.GlobalCacheKey(2);
            var oldKey3 = cache.CacheNamespace.GlobalCacheKey(3);

            var globalKeysKey = cache.CacheNamespace.GetGlobalKeysKey();

            // Act
            cache.Clear();

            // Assert
            
            // New generation.
            Assert.Equal(2, cache.CacheNamespace.GetGeneration());
            Assert.False(Redis.StringGet(cache.CacheNamespace.GlobalCacheKey(1)).HasValue);
            Assert.False(Redis.StringGet(cache.CacheNamespace.GlobalCacheKey(2)).HasValue);
            Assert.False(Redis.StringGet(cache.CacheNamespace.GlobalCacheKey(3)).HasValue);
            
            // List of keys for this region was cleared.
            Assert.False(Redis.StringGet(globalKeysKey).HasValue);

            // The old values will expire automatically.
            var ttl1 = Redis.KeyTimeToLive(oldKey1);
            Assert.True(ttl1 <= TimeSpan.FromMinutes(5));
            var ttl2 = Redis.KeyTimeToLive(oldKey2);
            Assert.True(ttl2 <= TimeSpan.FromMinutes(5));
            var ttl3 = Redis.KeyTimeToLive(oldKey3);
            Assert.True(ttl3 <= TimeSpan.FromMinutes(5));
        }

        [Fact]
        void Clear_should_ensure_generation_if_another_cache_has_already_incremented_the_generation()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);

            // Another cache updated its generation (by clearing).
            Redis.StringIncrement(cache.CacheNamespace.GetGenerationKey(), 100);

            // Act
            cache.Clear();

            // Assert
            Assert.Equal(102, cache.CacheNamespace.GetGeneration());
        }

        [Fact]
        void Destroy_should_not_clear()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);

            // Act
            cache.Destroy();

            // Assert
            Assert.Equal(1, cache.CacheNamespace.GetGeneration());
        }

        [Fact]
        void Lock_and_Unlock_concurrently_with_same_cache_client()
        {
            // Arrange
            var cache = new RedisCache("region", ConnectionMultiplexer, options);
            cache.Put(1, new Person("Foo", 1));

            var results = new ConcurrentQueue<string>();
            const int numberOfClients = 5;

            // Act
            var tasks = new List<Task>();
            for (var i = 1; i <= numberOfClients; i++)
            {
                int clientNumber = i;
                var t = Task.Factory.StartNew(() =>
                {
                    cache.Lock(1);
                    results.Enqueue(clientNumber + " lock");

                    // Atrifical concurrency.
                    Thread.Sleep(100);

                    results.Enqueue(clientNumber + " unlock");
                    cache.Unlock(1);
                });

                tasks.Add(t);
            }

            // Assert
            Task.WaitAll(tasks.ToArray());

            // Each Lock should be followed by its associated Unlock.
            var listResults = results.ToList();
            for (var i = 1; i <= numberOfClients; i++)
            {
                var lockIndex = listResults.IndexOf(i + " lock");
                Assert.Equal(i + " lock", listResults[lockIndex]);
                Assert.Equal(i + " unlock", listResults[lockIndex + 1]);
            }
        }

        [Fact]
        void Lock_and_Unlock_concurrently_with_different_cache_clients()
        {
            // Arrange
            var mainCache = new RedisCache("region", ConnectionMultiplexer, options);
            mainCache.Put(1, new Person("Foo", 1));

            var results = new ConcurrentQueue<string>();
            const int numberOfClients = 5;

            // Act
            var tasks = new List<Task>();
            for (var i = 1; i <= numberOfClients; i++)
            {
                int clientNumber = i;
                var t = Task.Factory.StartNew(() =>
                {
                    var cacheX = new RedisCache("region", ConnectionMultiplexer, options);
                    cacheX.Lock(1);
                    results.Enqueue(clientNumber + " lock");

                    // Atrifical concurrency.
                    Thread.Sleep(100);

                    results.Enqueue(clientNumber + " unlock");
                    cacheX.Unlock(1);
                });

                tasks.Add(t);
            }

            // Assert
            Task.WaitAll(tasks.ToArray());

            // Each Lock should be followed by its associated Unlock.
            var listResults = results.ToList();
            for (var i = 1; i <= numberOfClients; i++)
            {
                var lockIndex = listResults.IndexOf(i + " lock");
                Assert.Equal(i + " lock", listResults[lockIndex]);
                Assert.Equal(i + " unlock", listResults[lockIndex + 1]);
            }
        }

        [Fact]
        void Put_and_Get_should_silently_continue_if_SocketException()
        {
            using (var invalidConnectionMultiplexer = ConnectionMultiplexer.Connect(InvalidHost))
            {
                // Arrange
                const int key = 1;
                var cache = new RedisCache("region_A", invalidConnectionMultiplexer, options);

                // Act
                cache.Put(key, new Person("A", 1));

                // Assert
                Assert.Null(cache.Get(key));
            }
        }

        [Fact]
        void Lock_and_Unlock_should_silently_continue_if_SocketException()
        {
            using (var invalidConnectionMultiplexer = ConnectionMultiplexer.Connect(InvalidHost))
            {
                // Arrange
                const int key = 1;
                var cache = new RedisCache("region_A", invalidConnectionMultiplexer, options);

                // Act / Assert
                Assert.DoesNotThrow(() =>
                {
                    cache.Put(key, new Person("A", 1));
                    cache.Lock(key);
                    cache.Unlock(key);
                });
            }
        }

        [Fact]
        void Remove_should_silently_continue_if_SocketException()
        {
            using (var invalidConnectionMultiplexer = ConnectionMultiplexer.Connect(InvalidHost))
            {
                // Arrange
                const int key = 1;
                var cache = new RedisCache("region_A", invalidConnectionMultiplexer, options);

                // Act
                Assert.DoesNotThrow(() =>
                {
                    cache.Remove(key);
                });
            }
        }

        [Fact]
        void Should_update_server_generation_when_server_has_less_generation_than_the_client()
        {
            // Arrange
            const int key = 1;
            var cache = new RedisCache("region", ConnectionMultiplexer, options);

            // Act
            cache.Put(key, new Person("A", 1));
            FlushDb();
            cache.Put(key, new Person("B", 2));

            // Assert
            var generationKey = cache.CacheNamespace.GetGenerationKey();
            Assert.Equal(cache.CacheNamespace.GetGeneration().ToString(CultureInfo.InvariantCulture), (string)Redis.StringGet(generationKey));
        }
    }
}
