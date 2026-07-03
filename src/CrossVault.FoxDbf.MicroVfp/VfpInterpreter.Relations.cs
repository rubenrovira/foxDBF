using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>microVFP SET RELATION / SET SKIP — parent→child navigation, child repositioning, and RELATION()/TARGET().</summary>
public sealed partial class VfpInterpreter
{
    /// <summary>One parent→child link of a <c>SET RELATION</c>: the key expression (evaluated in the
    /// PARENT area on each parent move) plus the child work area it re-seeks. <see cref="OneToMany"/> is
    /// set by <c>SET SKIP</c>.</summary>
    private sealed class Relation
    {
        public PrgExpr Key = null!;    // relation key expression (evaluated in the PARENT area).
        public string KeyText = "";    // its raw source — SET("RELATION") / RELATION(n) read this back.
        public int ChildArea;          // target work-area number.
        public string ChildAlias = ""; // target alias (uppercase) — TARGET(n) / SET("RELATION").
        public bool OneToMany;         // SET SKIP marked this child one-to-many.
    }

    // ─────────────────────────── SET RELATION / SET SKIP (microVFP P1 gap #2) ───────────────────────────
    //
    // SET RELATION links the CURRENT (parent) area to child area(s). Each parent move auto-SEEKs the
    // relation key on the child's ACTIVE order (miss ⇒ child at EOF); a numeric key into a child with NO
    // controlling order does an implicit GOTO (record-number relation). Relations chain (a child may itself
    // be a parent) and there can be several per parent. SET SKIP marks a related child one-to-many: a SKIP
    // in that child then stays within the current parent-key group and goes EOF past its last matching row.

    private void ExecSetRelation(SetRelationStmt sr)
    {
        int area = Session.CurrentArea;
        if (area <= 0) return;
        var m = Meta(area);
        // Without ADDITIVE a new SET RELATION replaces the parent's prior relations; ADDITIVE appends.
        m.Relations = sr.Additive ? (m.Relations ?? new List<Relation>()) : new List<Relation>();
        foreach (var t in sr.Targets)
        {
            int child = ResolveAreaRef(t.Into);
            if (child <= 0) continue;
            var cwa = Session.AreaAt(child);
            // VFP9 orders relations MOST-RECENTLY-SET FIRST, for BOTH a single multi-target statement AND
            // cumulative ADDITIVE — so RELATION(1)/TARGET(1)/SET("RELATION") reflect the latest link. Insert
            // at the front (a multi-target statement's targets thus land reversed, matching vfp9.exe).
            m.Relations.Insert(0, new Relation
            {
                Key = t.Key,
                KeyText = t.Key.Text.Trim(),
                ChildArea = child,
                ChildAlias = cwa?.Alias ?? NameOf(t.Into),
            });
        }
        RepositionChildren(area);   // VFP positions the child(ren) immediately when the relation is set.
    }

    private void ExecSetRelationOff(SetRelationOffStmt sro)
    {
        int area = Session.CurrentArea;
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null) return;
        if (sro.Into is null) { m.Relations = null; return; }   // SET RELATION OFF (no target) ⇒ clear all.
        int child = ResolveAreaRef(sro.Into);
        m.Relations.RemoveAll(r => r.ChildArea == child);
    }

    private void ExecSetSkip(SetSkipStmt ss)
    {
        int area = Session.CurrentArea;
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null) return;
        if (ss.Aliases.Count == 0)                              // SET SKIP TO (no args) ⇒ clear all marks.
        {
            foreach (var r in m.Relations) r.OneToMany = false;
            return;
        }
        foreach (var nr in ss.Aliases)
        {
            int child = ResolveAreaRef(nr);
            foreach (var r in m.Relations) if (r.ChildArea == child) r.OneToMany = true;
        }
    }

    /// <summary>After a parent move, re-seek every child of <paramref name="parentArea"/> (recursively, so
    /// chained relations propagate). Guarded so the child moves it issues never re-fire the hook, and depth
    /// capped so a cyclic relation terminates. A no-op when the area has no relations.</summary>
    private void RepositionChildren(int parentArea)
    {
        if (_inReposition) return;
        if (!_meta.TryGetValue(parentArea, out var pm) || pm.Relations is null || pm.Relations.Count == 0) return;
        _inReposition = true;
        try { RepositionChildrenCore(parentArea, 0); }
        finally { _inReposition = false; }
    }

    private void RepositionChildrenCore(int parentArea, int depth)
    {
        if (depth > MaxRelationDepth) return;
        if (!_meta.TryGetValue(parentArea, out var pm) || pm.Relations is null) return;
        int prev = Session.CurrentArea;
        try
        {
            foreach (var rel in pm.Relations)
            {
                if (Session.AreaAt(rel.ChildArea) is null) continue;   // child closed → skip.
                Session.SelectArea(parentArea);
                var pmeta = Meta(parentArea);
                int prc = Session.AreaAt(parentArea)?.Table.RecordCount ?? 0;
                bool parentOnRecord = !pmeta.Eof && pmeta.RecNo >= 1 && pmeta.RecNo <= prc;
                if (!parentOnRecord)
                {
                    ForceEof(rel.ChildArea);                           // no parent record ⇒ child at EOF.
                }
                else
                {
                    var key = Eval(rel.Key);                           // evaluated in the PARENT area.
                    // A numeric key into a child with NO controlling order is a record-number relation
                    // (implicit GOTO); otherwise SEEK on the child's active order (a miss lands at EOF).
                    if (ActiveOrder(rel.ChildArea) is null && IsNumeric(key))
                        GoRecordCore(rel.ChildArea, (int)key.AsNumber);
                    else
                        DoSeekCore(key, rel.ChildArea, null, relationSeek: true);
                }
                Session.SelectArea(prev);
                RepositionChildrenCore(rel.ChildArea, depth + 1);      // chain: the child may be a parent.
            }
        }
        finally { Session.SelectArea(prev); }
    }

    /// <summary>Force <paramref name="area"/> to EOF (used when a parent has no current record, so its
    /// child — and transitively the grandchildren — cannot match).</summary>
    private void ForceEof(int area)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        m.RecNo = (wa?.Table.RecordCount ?? 0) + 1;
        m.Eof = true; m.Bof = false; m.Found = false; m.Cached = null; m.OldVals = null;
    }

    /// <summary>SET SKIP: when <paramref name="childArea"/> is a one-to-many child and a SKIP moved it off
    /// the record group matching the current parent key, clamp it to EOF (it walked past the last matching
    /// child row). No-op during reposition (that legitimately positions the child to its first match).</summary>
    private void ApplyOneToManyBound(int childArea)
    {
        if (_inReposition) return;
        var found = FindOneToManyParent(childArea);
        if (found is null) return;
        var (parentArea, rel) = found.Value;
        var m = Meta(childArea);
        if (m.Eof) return;
        int prev = Session.CurrentArea;
        try
        {
            Session.SelectArea(parentArea);
            var pmeta = Meta(parentArea);
            int prc = Session.AreaAt(parentArea)?.Table.RecordCount ?? 0;
            if (pmeta.Eof || pmeta.RecNo < 1 || pmeta.RecNo > prc) { ForceEof(childArea); return; }
            var parentKey = Eval(rel.Key);
            var childKey = ChildActiveKey(childArea);
            if (childKey is null || !RelationKeyMatches(parentKey, childKey.Value))
                ForceEof(childArea);
        }
        finally { Session.SelectArea(prev); }
    }

    /// <summary>The (parent area, relation) that marks <paramref name="childArea"/> one-to-many, or null.</summary>
    private (int ParentArea, Relation Rel)? FindOneToManyParent(int childArea)
    {
        foreach (var kv in _meta)
        {
            if (kv.Value.Relations is null) continue;
            foreach (var r in kv.Value.Relations)
                if (r.OneToMany && r.ChildArea == childArea) return (kv.Key, r);
        }
        return null;
    }

    /// <summary>The value of <paramref name="childArea"/>'s active-order key expression at its CURRENT
    /// record (null when the area has no controlling / first tag).</summary>
    private VfpValue? ChildActiveKey(int childArea)
    {
        var wa = Session.AreaAt(childArea);
        if (wa?.Cdx is null) return null;
        var m = Meta(childArea);
        string? tagName = string.IsNullOrEmpty(m.Order) ? wa.Cdx.TagNames.FirstOrDefault() : m.Order;
        if (tagName is null) return null;
        var tag = wa.Cdx.Tag(tagName) ?? wa.Cdx.Tag(tagName.ToUpperInvariant());
        if (tag is null) return null;
        int prev = Session.CurrentArea;
        try { Session.SelectArea(childArea); return EvalText(tag.KeyExpression); }
        finally { Session.SelectArea(prev); }
    }

    /// <summary>Whether a child key still belongs to the parent-key group: the relation SEEK is a PREFIX
    /// seek (child key STARTS WITH the parent key), so the group boundary uses the same prefix rule for
    /// character keys; non-character keys compare by value.</summary>
    private static bool RelationKeyMatches(VfpValue parentKey, VfpValue childKey)
    {
        if (parentKey.Type == VfpType.Character || childKey.Type == VfpType.Character)
            return childKey.AsString.StartsWith(parentKey.AsString.TrimEnd(), StringComparison.Ordinal);
        // Numeric/Integer/Currency/Date keys never populate the string backing, so an AsString comparison
        // was always ""=="" (true) — the one-to-many clamp then never fired. Compare by full typed value.
        return parentKey.Equals(childKey);
    }

    private string RelationSetString(int area)
    {
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null || m.Relations.Count == 0)
            return string.Empty;
        return string.Join(", ", m.Relations.Select(r => $"{r.KeyText} INTO {r.ChildAlias}"));
    }

    private string SkipSetString(int area)
    {
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null) return string.Empty;
        return string.Join(", ", m.Relations.Where(r => r.OneToMany).Select(r => r.ChildAlias));
    }

    private VfpValue FnRelation(VfpValue[] a)
    {
        int n = a.Length > 0 ? (int)a[0].AsNumber : 1;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var rels = _meta.TryGetValue(area, out var m) ? m.Relations : null;
        if (rels is null || n < 1 || n > rels.Count) return VfpValue.Character(string.Empty);
        return VfpValue.Character(rels[n - 1].KeyText);
    }

    private VfpValue FnTarget(VfpValue[] a)
    {
        int n = a.Length > 0 ? (int)a[0].AsNumber : 1;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var rels = _meta.TryGetValue(area, out var m) ? m.Relations : null;
        if (rels is null || n < 1 || n > rels.Count) return VfpValue.Character(string.Empty);
        return VfpValue.Character(rels[n - 1].ChildAlias);
    }

}
