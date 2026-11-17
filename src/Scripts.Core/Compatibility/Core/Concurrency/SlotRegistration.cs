// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// The handle returned by <see cref="ConcurrencyContext.RegisterSlot{T}"/> that names
/// a per-worker slot; the caller passes it to
/// <see cref="ConcurrencyContext.WorkerSlot{T}"/> to obtain the calling worker's
/// instance.
/// </summary>
/// <remarks>
/// <para>
/// The handle is opaque, created only by the context, and carries the slot's
/// factory and destroy callbacks. Keeping the handle separate from the value is
/// what makes the slot type-safe: the value is only reachable through the
/// generic <see cref="ConcurrencyContext.WorkerSlot{T}"/>, so a caller cannot read
/// a slot as the wrong type.
/// </para>
/// <para>
/// A slot's value for a worker is created the first time that worker asks for it
/// and lives until the context is disposed, so a worker's buffers, pools or
/// error collectors are allocated once, never per call.
/// </para>
/// </remarks>
public sealed class SlotRegistration
{
    internal SlotRegistration(Slot slot)
    {
        Slot = slot;
    }

    /// <summary>Gets the diagnostic name of the slot (the value type's name).</summary>
    public string Name => Slot.Name;

    /// <summary>Gets the underlying slot.</summary>
    internal Slot Slot { get; }
}
