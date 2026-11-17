// SPDX-FileCopyrightText: 2026 Younes BENMOUSSA <younes.benmoussa@pm.me>
// SPDX-License-Identifier: LGPL-3.0-or-later

namespace Core.Concurrency;

/// <summary>
/// One worker's part of a wave: a contiguous slice of a range, a claim-one cursor,
/// a chunked claim cursor, or a range of staged tasks.
/// </summary>
/// <remarks>
/// A wave is created by the dispatcher for one worker and dropped by that worker
/// when it is done; it is never shared. Its <see cref="FailureRecord"/> is the
/// one reference every worker of the call holds in common.
/// </remarks>
internal abstract class Wave
{
    /// <summary>
    /// Runs this worker's part and reports the failure it recorded, if any.
    /// </summary>
    internal abstract void Run();

    /// <summary>
    /// A contiguous index slice: <c>[start, end)</c> with a step.
    /// </summary>
    internal sealed class Slice : Wave
    {
        private readonly long _start;
        private readonly long _end;
        private readonly long _step;
        private readonly Action<long> _body;
        private readonly FailureRecord _failures;
        private readonly Action<Exception>? _abortAction;

        internal Slice(
            long start,
            long end,
            long step,
            Action<long> body,
            FailureRecord failures,
            Action<Exception>? abortAction)
        {
            _start = start;
            _end = end;
            _step = step;
            _body = body;
            _failures = failures;
            _abortAction = abortAction;
        }

        internal override void Run()
        {
            // Iterate by ordinal, not by adding `_step` to the previous index: at
            // the top of the long range `index += _step` wraps negative and the
            // loop never ends. The boundary is the exclusive end, so the loop runs
            // the indices `_start + k*_step` that are below it; `k` is bounded by
            // the slice length, which cannot overflow.
            var index = _start;

            while (index < _end)
            {
                if (!_failures.ShouldKeepGoing)
                {
                    return;
                }

                if (!Body.RunOne(_body, index, _failures, _abortAction))
                {
                    return;
                }

                var next = (Int128)index + _step;
                if (next >= _end)
                {
                    return;
                }

                index = (long)next;
            }
        }
    }

    /// <summary>
    /// A claim-one range: every worker pulls the next index from one cursor.
    /// </summary>
    internal sealed class Claim : Wave
    {
        private readonly ClaimCursor _cursor;
        private readonly Action<long> _body;
        private readonly FailureRecord _failures;
        private readonly Action<Exception>? _abortAction;

        internal Claim(
            ClaimCursor cursor,
            Action<long> body,
            FailureRecord failures,
            Action<Exception>? abortAction)
        {
            _cursor = cursor;
            _body = body;
            _failures = failures;
            _abortAction = abortAction;
        }

        internal override void Run()
        {
            while (_failures.ShouldKeepGoing && _cursor.TryClaim(out var index))
            {
                if (!Body.RunOne(_body, index, _failures, _abortAction))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A chunked claim range: every worker pulls a run of consecutive ordinals from
    /// one cursor, one atomic per run.
    /// </summary>
    /// <remarks>
    /// The lowest-index failure stays deterministic: the cursor hands out
    /// ordinals in strictly increasing blocks, so a lower ordinal is always
    /// claimed before a higher one and the failing item with the lowest index is
    /// the one the record reports.
    /// </remarks>
    internal sealed class ClaimChunk : Wave
    {
        private readonly ChunkCursor _cursor;
        private readonly int _chunkSize;
        private readonly Action<long> _body;
        private readonly FailureRecord _failures;
        private readonly Action<Exception>? _abortAction;

        internal ClaimChunk(
            ChunkCursor cursor,
            int chunkSize,
            Action<long> body,
            FailureRecord failures,
            Action<Exception>? abortAction)
        {
            _cursor = cursor;
            _chunkSize = chunkSize;
            _body = body;
            _failures = failures;
            _abortAction = abortAction;
        }

        internal override void Run()
        {
            while (_failures.ShouldKeepGoing && _cursor.TryClaim(_chunkSize, out var first, out var length))
            {
                // One chunk is a contiguous run of ordinals; a failure inside it
                // stops this worker at the failing item, and every index is mapped
                // through Int128 so a range near the type's edge never wraps.
                for (var offset = 0; offset < length; offset++)
                {
                    if (!Body.RunOne(_body, _cursor.IndexAt(first + offset), _failures, _abortAction))
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// A range of staged tasks this worker runs in staging order.
    /// </summary>
    internal sealed class Tasks : Wave
    {
        private readonly Action[] _tasks;
        private readonly int _start;
        private readonly int _end;
        private readonly FailureRecord _failures;

        internal Tasks(Action[] tasks, int start, int end, FailureRecord failures)
        {
            _tasks = tasks;
            _start = start;
            _end = end;
            _failures = failures;
        }

        internal override void Run()
        {
            // The range is walked in full: DispatchAndWait always collects every
            // failure, and Run hands this wave exactly one task, so there is no
            // stop to observe between tasks here.
            for (var index = _start; index < _end; index++)
            {
                try
                {
                    _tasks[index]();
                }
                catch (Exception exception)
                {
                    _failures.Record(index, exception);
                }
            }
        }
    }
}
