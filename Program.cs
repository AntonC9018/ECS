using System;
using static System.Diagnostics.Debug;
using Kari.Plugins.DataObject;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using BF = Kari.Plugins.Bitfield;
using Stuff.Generated;

namespace Stuff
{
    public static class H
    {
        public static byte CountBits(int number) => CountBits((uint) number);
        public static byte CountBits(uint number)
        {
            uint a = 0;
            for (int i = 0; i < 8; i++)
                a += (number >> i) & 0x01010101;
            byte b = 0;
            for (int i = 0; i < 4; i++)
                b += (byte) ((a >> (8 * i)) & 0x000000ff);
            return b;
        }

        public static byte CountBits(short number) => CountBits((ushort) number);
        public static byte CountBits(ushort number)
        {
            ushort a = 0;
            for (int i = 0; i < 8; i++)
                a += (ushort)((number >> i) & 0x0101);
            byte b = 0;
            for (int i = 0; i < 2; i++)
                b += (byte) ((a >> (8 * i)) & 0x00ff);
            return b;
        }
    }

    [DataObject]
    public partial struct EntityId //: IVersion
    {
        public uint id;
        public EntityId(uint id) { this.id = id; }

        public ushort Version => (ushort) (id & 0x0000ffff);
        public ushort Slot => (ushort) (id >> 16);
    }

    public interface IItemPool
    {
        bool DoesEntityOwnSlot(EntityId entity);
        void ReleaseItem(EntityId entity);
        void AllocateItem(EntityId entity);
        void TryReleaseItem(EntityId entity)
        {
            if (DoesEntityOwnSlot(entity))
                ReleaseItem(entity);
        }
    }

    public class SparseSet<ItemType> : IItemPool where ItemType : struct
    {
        public ushort[] _slotToInnerIndex; // sparse
        public EntityId[] _innerIndexToEntityId; // dense
        public ItemType[] _items; // dense, indexed by the inner index
        private int _count = 0;
        public int MaxNumEntities => _slotToInnerIndex.Length;

        public SparseSet(int capacityPowerOfTwo, int initialDenseSize = 1)
        {
            Assert(H.CountBits(capacityPowerOfTwo) == 1);
            Assert(H.CountBits(initialDenseSize) == 1);

            _slotToInnerIndex = new ushort[capacityPowerOfTwo];
            _innerIndexToEntityId = new EntityId[initialDenseSize];
            Array.Fill(_innerIndexToEntityId, new EntityId(0xffffffff));
            // TODO: not final
            _items = new ItemType[initialDenseSize];
        }

        public bool DoesEntityOwnSlot(EntityId entity)
        {
            Assert(entity.Slot < MaxNumEntities);
            var innerIndex = _slotToInnerIndex[entity.Slot];
            return _innerIndexToEntityId[innerIndex] == entity;
        }

        public ref ItemType this[EntityId entity]
        {
            get 
            {
                Assert(entity.Slot < MaxNumEntities);
                var innerIndex = _slotToInnerIndex[entity.Slot];
                Assert(_innerIndexToEntityId[innerIndex] == entity);
                return ref _items[innerIndex];
            }
        }

        public Span<ItemType> Items() => _items.AsSpan(0, _count);

        public ref ItemType AllocateItem(EntityId entity)
        {
            Assert(entity.Slot < MaxNumEntities);
            Assert(!DoesEntityOwnSlot(entity));
            // We may be out of space, then it's bad.
            // Might as well just crash.
            Assert(_count <= ushort.MaxValue);

            if (_count == _innerIndexToEntityId.Length)
            {
                // This is pretty expensive obviously
                Array.Resize(ref _innerIndexToEntityId, _count * 2);
                Array.Resize(ref _items, _count * 2);
                Assert(_items.Length == _innerIndexToEntityId.Length);
            }

            // The index is going to be used now.
            _innerIndexToEntityId[_count] = entity;
            _slotToInnerIndex[entity.Slot] = (ushort) _count;

            return ref _items[_count++];
        }
        void IItemPool.AllocateItem(EntityId entity) => AllocateItem(entity); 

        public void ReleaseItem(EntityId entity)
        {
            Assert(entity.Slot < MaxNumEntities);
            Assert(DoesEntityOwnSlot(entity));
            Assert(_count > 0);

            uint innerIndex = _slotToInnerIndex[entity.Slot];
            EntityId lastEntity = _innerIndexToEntityId[_count - 1];
            _slotToInnerIndex[lastEntity.Slot] = (ushort) innerIndex;
            _innerIndexToEntityId[innerIndex] = lastEntity;
            _items[innerIndex] = _items[_count - 1];
            _count--;
        }
    }

    public struct EntityRegistryEntry
    {
        public ushort next;
        public ushort version;
        // Some systems may prefer to keep the entity around even after it dies.
        // For example, the renderer may want to play some animation after an entity dies.
        // We release a particular entity component only if 
        public uint keeparoundCount;
        public bool isDead; // 
    }

    public class ThingRegistry<ThingType> : List<ThingType>
    {
        public new uint Add(ThingType item) 
        { 
            base.Add(item); 
            return (uint)(Count - 1); 
        }
    }

    public readonly struct PoolHandle<T> 
    { 
        public readonly int id;
        public PoolHandle(int id) { this.id = id; }
    }
    
    public class PoolRegistry
    {
        public uint[] _poolIsKeeparound = new uint[1];
        public List<IItemPool> _pools = new List<IItemPool>(32);
        public int Count => _pools.Count;


        /// Returns the pool handle
        public PoolHandle<T> AddPool<T>(T itemPool, bool isKeeparound = false) where T : IItemPool
        {
            int index = _pools.Count;
            _pools.Add(itemPool);
            if (_poolIsKeeparound.Length < _pools.Count * 32)
                Array.Resize(ref _poolIsKeeparound, _poolIsKeeparound.Length + 1);

            if (isKeeparound)
            {
                int intIndex = index / 32;
                int bitIndex = index - intIndex * 32;
                _poolIsKeeparound[intIndex] &= 1u << bitIndex;
            }

            return new PoolHandle<T>(index);
        }

        public T GetPool<T>(PoolHandle<T> handle)
        {
            Assert(handle.id < _pools.Count);
            Assert(_pools[handle.id] is T);
            return (T) _pools[handle.id];
        }

        public bool IsPoolRefcounted(int index)
        {
            Assert(index < _pools.Count);
            int intIndex = index / 32;
            int bitIndex = index - intIndex * 32;
            return ((_poolIsKeeparound[intIndex] >> bitIndex) & 1) == 1;
        }
    }

    public class EntityIdRegistry
    {
        // TODO: I should pass this in explicitly as a parameter
        public PoolRegistry _pools;
        public EntityRegistryEntry[] _entityRegistryEntries;
        public uint _freelist;
        public uint _numEntities;
        public int NumEntities => (int) _numEntities;

        public EntityIdRegistry(int maxNumEntities, PoolRegistry pools)
        {
            Assert(H.CountBits(maxNumEntities) == 1);
            Assert(maxNumEntities <= ushort.MaxValue);
            _entityRegistryEntries = new EntityRegistryEntry[maxNumEntities];
            for (int i = 0; i < maxNumEntities; i++)
            {
                ref var entry = ref _entityRegistryEntries[i];
                entry.next = (ushort) (i + 1);
                entry.version = 0;
                entry.isDead = false;
                entry.keeparoundCount = 0;
            }

            _numEntities = 0;
            _freelist = 0;
            _pools = pools;
        }

        public EntityId CreateEntity()
        {
            ref var entry = ref _entityRegistryEntries[_freelist];
            var id = new EntityId((uint) entry.version | (_freelist << 16));
            entry.isDead = false;
            entry.keeparoundCount = 0;
            _freelist = entry.next;
            _numEntities++;
            return id;
        }

        public ref EntityRegistryEntry GetEntry(EntityId entity)
        {
            Assert(entity.Slot < _entityRegistryEntries.Length);
            return ref _entityRegistryEntries[entity.Slot];
        }

        // TODO: unsure yet
        // public ref ItemType AssociateComponentWithEntity<T, ItemType>(PoolHandle<T> componentPoolHandle, EntityId entity) 
        //     where T : SparseSet<ItemType> 
        //     where ItemType : struct
        // {
        //     var pool = _pools.GetPool<T>(componentPoolHandle);
        //     if (_pools.IsPoolRefcounted(componentPoolHandle.id))
        //         GetEntry(entity).refcount++;
        //     return ref pool.AllocateItem(entity);
        // }

        public void SetEntityDead(EntityId entity)
        {
            Assert(GetEntry(entity).isDead == false);
            GetEntry(entity).isDead = true;
            // TODO: kick off subscribed handlers.

            // TODO??: Probably remove unrefcount components once this gets hit.
            for (int i = 0; i < _pools.Count; i++)
            {
                if (!_pools.IsPoolRefcounted(i))
                    _pools._pools[i].TryReleaseItem(entity);
            }
        }

        // TODO: in debug, store which components do this.
        public uint DecreaseRefcount(EntityId entity)
        {
            ref var entry = ref GetEntry(entity);
            Assert(entry.keeparoundCount > 0);
            return --entry.keeparoundCount;
        }

        public void RemoveEntity(EntityId entity)
        {
            ref var entry = ref GetEntry(entity);
            Assert(entry.keeparoundCount == 0 && entry.isDead);
            _entityRegistryEntries[_freelist].next = entity.Slot;
            _freelist = entity.Slot;
            _numEntities--;
            
            // It's fine if the version loops around, I think.
            entry.version++;
            entry.version %= 0xffff;

            // For now, make sure all components were removed
            for (int i = 0; i < _pools.Count; i++)
                Assert(!_pools._pools[i].DoesEntityOwnSlot(entity));
        }

        public bool IsEntityRemoved(EntityId entity)
        {
            return GetEntry(entity).version != entity.Version;
        }

        public bool IsEntityDead(EntityId entity)
        {
            return GetEntry(entity).isDead;
        }

        public bool IsEntityRemovedOrDead(EntityId entity)
        {
            ref var entry = ref GetEntry(entity);
            return entry.isDead || entry.version != entity.Version;
        }
    }

    public interface IMessageDispatcher
    {
        void Dispatch(Span<byte> messageBytes);
    }

    public interface IEventDispatcher<TMessageData> : IMessageDispatcher
    {
        void Dispatch(ref TMessageData message); 
    }

    public interface ISubscriber<TMessageData>
    {
        bool Handle(ref TMessageData message);
    }

    [DataObject]
    public readonly partial struct SubscriberData<THandler>
    {
        public readonly int Metadata;
        public readonly THandler Handler;
        public SubscriberData(int metadata, THandler handler)
        {
            Metadata = metadata;
            Handler = handler;
        }
    }

    [BF.Specification("MessageIdentifier")]
    public interface IMessageIdentifier
    {
        [BF.Bit] bool IsBroadcastable { get; set; }
        [BF.Bit] bool IsDirect { get; set; }
        [BF.Bit] bool IsSelfReference { get; set; }
        [BF.Rest] int Number { get; set; }
    }

    public readonly struct MessageTypeIndex<TMessageData>
    {
        readonly MessageIdentifier identifier;
    }

    // Self sufficient data structure for events
    public struct CopyOnWriteSet<TElement>
    {
        public TElement[] _array;
        public int _count;
        public bool _hasBeenCopied;
        /// Whether has been changed recently
        public bool _isDirty;

        public int Capacity => _array.Length;
        public int Count => _count;

        /// Sorts the array using the given comparer, unsets dirty. 
        public void Tidy(System.Comparison<TElement> func)
        {
            if (!_isDirty)
                return;
            Elements.Sort(func);
            _isDirty = false;
        }

        public Span<TElement> Elements => _array.AsSpan(0, _count);

        public void Add(TElement element)
        {
            if (_count == Capacity)
            {
                Array.Resize(ref _array, Capacity * 2);
                _hasBeenCopied = true;
            }
            _array[_count] = element;
            _count++;
            _isDirty = true;
        }

        public void Add(Span<TElement> elements)
        {
            // When adding a 0 length thing, we must not set the dirty flag.
            if (elements.Length == 0)
                return;
            int newCount = elements.Length + _count;
            if (newCount > Capacity)
            {
                Array.Resize(ref _array, Math.Max(newCount, Capacity * 2));
                _hasBeenCopied = true;
            }
            elements.CopyTo(_array.AsSpan(_count, newCount - _count));
            _count = newCount;
            _isDirty = true;
        }

        public void Remove(int index)
        {
            Assert(index < _count);
            if (index < _count - 1)
            {
                if (!_hasBeenCopied)
                {
                    var oldArray = _array;
                    _array = new TElement[Capacity];
                    Array.Copy(oldArray, _array, _count);
                    _hasBeenCopied = true;
                }
                _array[index] = _array[_count - 1];
                _isDirty = true;
            }
            _count--;
        }

        public void Remove<Eq>(in TElement item, Eq comparer) where Eq : IEqualityComparer<TElement>
        {
            int index = 0;
            do { Assert(index < _count); }
            while (comparer.Equals(item, _array[index++]));
            Remove(index);
        }

        public void TryRemove<Eq>(in TElement item, Eq comparer) where Eq : IEqualityComparer<TElement>
        {
            for (int index = 0; index < _count; index++)
            {
                if (comparer.Equals(item, _array[index]))
                {
                    Remove(index);
                    break;
                }
            }
        }
    }

    public class ListWithDirtyFlag<TElement>
    {
        public TElement[] _array;
        public int _count;
        public bool _isDirty;
        public int Capacity => _array.Length;
        public int Count => _count;


        public void Tidy(System.Comparison<TElement> func)
        {
            if (!_isDirty)
                return;
            _array.AsSpan().Sort(func);
            _isDirty = false;
        }

        public void Add(TElement element)
        {
            if (_count == Capacity)
                Array.Resize(ref _array, Capacity * 2);
            _array[_count] = element;
            _count++;
            _isDirty = true;
        }

        public void Add(Span<TElement> elements)
        {
            if (elements.Length == 0)
                return;
            int newCount = elements.Length + _count;
            if (newCount > Capacity)
                Array.Resize(ref _array, Math.Max(newCount, Capacity * 2));
            elements.CopyTo(_array.AsSpan(_count, newCount - _count));
            _count = newCount;
            _isDirty = true;
        }

        public void Remove(int index)
        {
            Assert(index < _count);
            if (index < _count - 1)
            {
                _array[index] = _array[_count - 1];
                _isDirty = true;
            }
            _count--;
        }

        public void Remove<Eq>(in TElement item, Eq comparer) where Eq : EqualityComparer<TElement>
        {
            int index = 0;
            do { Assert(index < _count); }
            while (comparer.Equals(item, _array[index++]));
            Remove(index);
        }

        public void TryRemove<Eq>(in TElement item, Eq comparer) where Eq : IEqualityComparer<TElement>
        {
            for (int index = 0; index < _count; index++)
            {
                if (comparer.Equals(item, _array[index]))
                {
                    Remove(index);
                    break;
                }
            }
        }
    }

    // The types of messages:
    //
    // 1. (Only) Self reference. Means when this is called, only own handlers are called.
    //    Can be handled on its own by the systems, stored in components.
    //    If the default handlers are not going to change, and new ones may be added, this can be split
    //    in two parts, then iterate both of them in lockstep by comparing the priorities.
    //    Only the latter array will be ever modified (can be a list or even something fancier 
    //    if it's expected to change much). Thing is, this can be handled by the systems, stored in the
    //    components by those systems, and it's no big deal.
    // 
    // 2. Direct messages. Presumably, does not need any handlers????
    //
    // 3. Broadcasted messages. Means a message is dispatched and anyone can see it.
    //    This can be split even further: the listeners may only be albe to listen to such message
    //    from any entity, or a specfic entity. Of course, we could do filtering here (make all such messages
    //    "global" and then just check if come from a given entity). I'm going to try something some fancy though.


    // public readonly struct BroadcastableEventHandlerHandle
    // {
    //     public readonly EntityId broadcasterId;
    //     public readonly uint ;
    // }

    // One such table will exist per broadcastable event.
    // Listeners of broadcastable events do not respect priority.
    // The handlers will be iterated by entities.
    // Otherwise, the problem gets out of hand, because you need to keep the sets sorted

    // Workflow:
    // 1. Users push events into the message pipe.
    // 2. The next event gets processed, its type get determined.
    // 3. The associated handler gets called with the rest of the queue after the message header.
    //    It takes the message part off the span, modifying it.
    // 4. That thing know how to dispatch the message properly.
    //      - if the message is direct, it does some predefined function probably.
    //      - if the message is self-referencial, it finds the component of the entity
    //        and calls the listener queue, or multiple queues (one readonly, one mutable).
    //      - if the message is broadcastable, it reaches into the registry, grabs the data structure
    //        with the listeners, calls the listeners. So listeners need to be associated with 
    //        the given broadcaster. It would also call the all listeners.
    //    So in the end, the data structure never calls the listeners. The initial message handler
    //    always knows what things to invoke itself. It just stores something.
    // 
    // So let's do the simplest thing for now:
    // 1. A dictionary for handler data (priority, callback, whatnot).
    // 2. An array with isdirty flag.
    public class BroadcastEventTable<THandlerCollection>
    {
        // Maps broadcaster id -> subscribed handlers
        public Dictionary<EntityId, THandlerCollection> HandlerCollections;
        public THandlerCollection GlobalHandlerCollection;
    }

    class Program
    {
        public struct Acomponent { public int a; public int b; }
        public struct Bcomponent { public int a; public int b; }

        static void Main(string[] args)
        {
            var pools = new PoolRegistry();
            var aHandle = pools.AddPool(new SparseSet<Acomponent>(16));
            var bHandle = pools.AddPool(new SparseSet<Bcomponent>(16));

            var registry = new EntityIdRegistry(16, pools);
            var entity0 = registry.CreateEntity();
            Assert(registry.NumEntities == 1);

            var aPool = pools.GetPool(aHandle);
            ref var a = ref aPool.AllocateItem(entity0);
            Assert(aPool.DoesEntityOwnSlot(entity0));
            ref var otherA = ref aPool[entity0];
            unsafe { Assert(Unsafe.AsPointer(ref a) == Unsafe.AsPointer(ref otherA)); }
            
            var entity1 = registry.CreateEntity();
            ref var a1 = ref pools.GetPool(aHandle).AllocateItem(entity1);

            unsafe { Assert(Unsafe.AsPointer(ref a) != Unsafe.AsPointer(ref a1)); }

            registry.SetEntityDead(entity0);
            // The component got deleted
            Assert(!pools.GetPool(aHandle).DoesEntityOwnSlot(entity0));
            Assert(!pools.GetPool(bHandle).DoesEntityOwnSlot(entity0));
            Assert(registry.IsEntityDead(entity0));

            registry.RemoveEntity(entity0);
            Assert(registry.IsEntityRemoved(entity0));

            // registry.GetEntry(entity1).refcount = 1;
            // registry.SetEntityDead(entity1);
            // Assert(!pools.GetPool(aHandle).DoesEntityOwnSlot(entity1));
        }
    }
}