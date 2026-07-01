using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>The parameter collection for a <see cref="FoxDbfCommand"/>, backed by an ordered list so
/// positional (<c>?</c>) binding maps by index and named binding maps by <see cref="DbParameter.ParameterName"/>.</summary>
public sealed class FoxDbfParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = new();
    private readonly object _sync = new();

    /// <summary>Convenience add (mirrors <c>SqlParameterCollection.AddWithValue</c>): create a
    /// <see cref="FoxDbfParameter"/> from <paramref name="parameterName"/> + <paramref name="value"/>,
    /// append it, and return it.</summary>
    public FoxDbfParameter AddWithValue(string parameterName, object? value)
    {
        var p = new FoxDbfParameter(parameterName, value);
        _items.Add(p);
        return p;
    }

    public override int Count => _items.Count;
    public override object SyncRoot => _sync;

    public override int Add(object value)
    {
        _items.Add(Cast(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values) _items.Add(Cast(v!));
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains(Cast(value));

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        int i = IndexOf(parameterName);
        if (i < 0) throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
        return _items[i];
    }

    public override int IndexOf(object value) => _items.IndexOf(Cast(value));

    public override int IndexOf(string parameterName)
    {
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    public override void Remove(object value) => _items.Remove(Cast(value));

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        int i = IndexOf(parameterName);
        if (i < 0) throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
        _items.RemoveAt(i);
    }

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int i = IndexOf(parameterName);
        if (i < 0) _items.Add(value);
        else _items[i] = value;
    }

    private static DbParameter Cast(object value)
        => value as DbParameter ?? throw new InvalidCastException("Parameter must be a DbParameter.");
}
