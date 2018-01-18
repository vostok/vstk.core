using FluentAssertions;
using NUnit.Framework;
using Vostok.Clusterclient.Ordering.Storage;

namespace Vostok.ClusterClient.Tests.Core.Ordering.Storage
{
    public class PerInstanceReplicaStorageProvider_Tests
    {
        
        [TestCase(null)]
        [TestCase("")]
        [TestCase("key")]
        public void Two_different_instances_should_return_different_storages_for_same_storage_key(string storageKey)
        {
            var provider1 = new PerInstanceReplicaStorageProvider();
            var provider2 = new PerInstanceReplicaStorageProvider();

            var storage1 = provider1.Obtain<int>();
            var storage2 = provider2.Obtain<int>();

            storage2.Should().NotBeSameAs(storage1);
        }
    }
}