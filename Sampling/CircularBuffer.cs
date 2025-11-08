using CommunityToolkit.HighPerformance;
using System.Collections;

namespace Faxtract.Sampling;

/// <summary>
/// A fixed-capacity circular buffer that provides efficient indexed access and sequence matching.
/// Never grows beyond its capacity - oldest items are automatically overwritten.
/// </summary>
/// <typeparam name="T">The type of elements in the buffer</typeparam>
internal class CircularBuffer<T> : IEnumerable<T>
{
    private T[] _buffer;
    private int _start;  // Index of the oldest element
    private int _count;  // Number of elements currently in the buffer

    /// <summary>
    /// Gets the maximum capacity of the buffer
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the number of elements currently in the buffer
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Gets whether the buffer is empty
    /// </summary>
    public bool IsEmpty => _count == 0;

    /// <summary>
    /// Gets whether the buffer is full
    /// </summary>
    public bool IsFull => _count == Capacity;

    /// <summary>
    /// Creates a new circular buffer with the specified capacity
    /// </summary>
    public CircularBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be positive", nameof(capacity));

        _buffer = new T[capacity];
        _start = 0;
        _count = 0;
    }

    /// <summary>
    /// Adds an item to the buffer. If full, overwrites the oldest item.
    /// </summary>
    public void Add(T item)
    {
        if (_count < Capacity)
        {
            // Buffer not full yet - add to end
            var index = (_start + _count) % Capacity;
            _buffer[index] = item;
            _count++;
        }
        else
        {
            // Buffer is full - overwrite oldest and advance start
            _buffer[_start] = item;
            _start = (_start + 1) % Capacity;
        }
    }

    /// <summary>
    /// Gets the element at the specified index (0 is oldest, Count-1 is newest)
    /// </summary>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();

            var actualIndex = (_start + index) % Capacity;
            return _buffer[actualIndex];
        }
    }

    /// <summary>
    /// Checks if the most recent 'length' elements match the given sequence.
    /// Returns true if the tail of the buffer matches the sequence.
    /// </summary>
    public bool TailMatches(ReadOnlySpan<T> sequence)
    {
        if (sequence.Length == 0 || sequence.Length > _count)
            return false;

        var startIdx = _count - sequence.Length;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < sequence.Length; i++)
        {
            if (!comparer.Equals(this[startIdx + i], sequence[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if the most recent elements match any prefix of the given sequence.
    /// Returns the length of the matching prefix (0 if no match, sequence.Length if complete match).
    /// </summary>
    public int GetMatchingPrefixLength(ReadOnlySpan<T> sequence)
    {
        if (sequence.Length == 0 || _count == 0)
            return 0;

        var maxPrefix = Math.Min(_count, sequence.Length);
        var comparer = EqualityComparer<T>.Default;

        // Check from longest to shortest prefix for efficiency
        for (var prefixLen = maxPrefix; prefixLen >= 1; prefixLen--)
        {
            var startIdx = _count - prefixLen;
            var matches = true;

            for (var i = 0; i < prefixLen; i++)
            {
                if (!comparer.Equals(this[startIdx + i], sequence[i]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return prefixLen;
        }

        return 0;
    }

    /// <summary>
    /// Removes the most recently added (newest) element from the buffer.
    /// Returns the element if one was removed or T's default value if the buffer was empty.
    /// Overwritten/evicted elements (from prior overwrites) cannot be recovered.
    /// </summary>
    public T? RemoveLast()
    {
        if (_count == 0)
            return default;

        _count--;
        var index = (_start + _count) % Capacity;
        var ret = _buffer[index];

        // Clear reference to allow GC for reference types
        if (!typeof(T).IsValueType)
        {
            _buffer[index] = default!;
        }

        return ret;
    }

    /// <summary>
    /// Removes the least recently added (oldest) element from the buffer.
    /// Returns the element if one was removed or T's default value if the buffer was empty.
    /// Overwritten/evicted elements (from prior overwrites) cannot be recovered.
    /// </summary>
    public T? RemoveFirst()
    {
        if (_count == 0)
            return default;

        var ret = _buffer[_start];
        _start = (_start + 1) % Capacity;
        _count--;

        // Clear reference to allow GC for reference types
        if (!typeof(T).IsValueType)
        {
            var clearIndex = (_start + Capacity - 1) % Capacity;
            _buffer[clearIndex] = default!;
        }

        return ret;
    }

    public T? PeekLast()
    {
        if (_count == 0)
            return default;

        var index = (_start + _count - 1) % Capacity;
        return _buffer[index];
    }

    /// <summary>
    /// Resizes the buffer to a new capacity, preserving the most recent elements.
    /// If new capacity is smaller, only the most recent elements are kept.
    /// </summary>
    public void Resize(int newCapacity)
    {
        if (newCapacity <= 0)
            throw new ArgumentException("Capacity must be positive", nameof(newCapacity));

        if (newCapacity == Capacity)
            return;

        var newBuffer = new T[newCapacity];
        var itemsToKeep = Math.Min(_count, newCapacity);

        if (itemsToKeep > 0)
        {
            // Copy the most recent items
            var sourceStart = _count - itemsToKeep;
            for (var i = 0; i < itemsToKeep; i++)
            {
                newBuffer[i] = this[sourceStart + i];
            }
        }

        _buffer = newBuffer;
        _start = 0;
        _count = itemsToKeep;
    }

    /// <summary>
    /// Clears all elements from the buffer
    /// </summary>
    public void Clear()
    {
        _start = 0;
        _count = 0;
        // Optional: clear the array for GC if T contains references
        if (!typeof(T).IsValueType)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }

    /// <summary>
    /// Copies the buffer contents to a new array (oldest to newest)
    /// </summary>
    public T[] ToArray()
    {
        var result = new T[_count];
        for (var i = 0; i < _count; i++)
        {
            result[i] = this[i];
        }
        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}