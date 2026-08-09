using ApexLab.Application.Canonical;
using ApexLab.Domain.Laps;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Application.Laps;

public static class BahrainLapCandidateAssembler
{
    public static async Task<BahrainLapInventory> AssembleAsync(
        CanonicalCacheCompletion completion,
        IAsyncEnumerable<CanonicalRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new AssemblyState(completion);
        await foreach (var record in records
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Accept(record);
        }

        return state.Complete();
    }

    private sealed class AssemblyState
    {
        private readonly CanonicalCacheCompletion _completion;
        private readonly List<BahrainLapCandidate> _candidates = [];
        private BahrainTelemetryContext? _currentContext;
        private BahrainTelemetryContext? _referenceContext;
        private SegmentState? _segment;
        private long _nextExpectedSequence = 1;
        private bool _sequenceExhausted;
        private long _recordCount;
        private long _observationCount;
        private long _exclusionCount;
        private long _gapCount;
        private long? _firstSourceSequence;
        private long? _lastSourceSequence;

        public AssemblyState(CanonicalCacheCompletion completion) =>
            _completion = completion;

        public void Accept(CanonicalRecord record)
        {
            ValidateAndAdvanceCoverage(record);
            _recordCount++;
            switch (record.Kind)
            {
                case CanonicalRecordKind.Observation:
                    _observationCount++;
                    AcceptObservation(record);
                    break;
                case CanonicalRecordKind.Exclusion:
                    _exclusionCount++;
                    break;
                case CanonicalRecordKind.Gap:
                    _gapCount++;
                    if (_segment is not null)
                    {
                        _segment.Flags |= LapEvidenceFlags.MaterialGapObserved;
                    }

                    break;
                default:
                    throw MalformedOrder();
            }
        }

        public BahrainLapInventory Complete()
        {
            ValidateCompletion();
            if (_segment is not null)
            {
                if (_sequenceExhausted)
                {
                    throw new BahrainLapAssemblyException(
                        BahrainLapAssemblyFailureKind.SequenceExhausted,
                        "The final canonical sequence cannot form a half-open lap boundary.");
                }

                AddPartialCandidate(
                    _segment.IsLeading
                        ? LapBoundaryCompleteness.LeadingPartial
                        : LapBoundaryCompleteness.TrailingPartial,
                    _nextExpectedSequence);
            }

            return new(
                _completion.Identity.IdentitySha256,
                _completion.CanonicalSha256,
                _referenceContext,
                _candidates);
        }

        private void AcceptObservation(CanonicalRecord record)
        {
            var packet = record.Packet
                ?? throw MalformedOrder();
            var sequence = record.SourceSequence
                ?? throw MalformedOrder();
            MarkPlayerChange(packet.Header);
            switch (packet.Family)
            {
                case CanonicalPacketFamily.Session when packet.Session is { } session:
                    AcceptSession(packet.Header, session);
                    break;
                case CanonicalPacketFamily.LapData when packet.Lap is { } lap:
                    AcceptLap(sequence, packet.Header, lap);
                    break;
                case CanonicalPacketFamily.Event
                    when packet.Event is { Kind: CanonicalEventKind.Flashback }:
                    if (_segment is not null)
                    {
                        _segment.Flags |= LapEvidenceFlags.FlashbackObserved;
                    }

                    break;
                case CanonicalPacketFamily.Motion or CanonicalPacketFamily.CarTelemetry:
                    break;
                default:
                    throw MalformedOrder();
            }
        }

        private void AcceptSession(
            CanonicalPacketHeader header,
            CanonicalSessionPacket session)
        {
            var context = new BahrainTelemetryContext(
                session,
                header.PlayerCarIndex,
                header.SecondaryPlayerCarIndex);
            if (_segment is not null)
            {
                if (_segment.Context is null)
                {
                    _segment.Flags |= LapEvidenceFlags.MissingContext;
                }
                else if (_segment.Context != context)
                {
                    _segment.Flags |= LapEvidenceFlags.ContextChanged;
                }

                if (!context.IsSupported)
                {
                    _segment.Flags |= LapEvidenceFlags.UnsupportedContext;
                }
            }

            _currentContext = context;
            if (_referenceContext is null && context.IsSupported)
            {
                _referenceContext = context;
            }
        }

        private void AcceptLap(
            long sequence,
            CanonicalPacketHeader header,
            CanonicalLapPacket lap)
        {
            if (_segment is null)
            {
                StartSegment(sequence, header, lap.CurrentLapNumber, isLeading: true);
            }
            else if (lap.CurrentLapNumber != _segment.LapNumber)
            {
                var exactIncrement = _segment.LapNumber != byte.MaxValue
                    && lap.CurrentLapNumber == _segment.LapNumber + 1;
                if (!exactIncrement || lap.LastLapTimeMilliseconds == 0)
                {
                    AddPartialCandidate(
                        LapBoundaryCompleteness.Incoherent,
                        sequence);
                }
                else if (_segment.IsLeading)
                {
                    AddPartialCandidate(
                        LapBoundaryCompleteness.LeadingPartial,
                        sequence);
                }
                else
                {
                    AddCompleteCandidate(
                        sequence,
                        lap.LastLapTimeMilliseconds);
                }

                StartSegment(sequence, header, lap.CurrentLapNumber, isLeading: false);
            }

            var active = _segment ?? throw MalformedOrder();
            if (lap.CurrentLapInvalid)
            {
                active.Flags |= LapEvidenceFlags.InvalidationObserved;
            }

            if (lap.PitStatus != 0)
            {
                active.Flags |= LapEvidenceFlags.PitObserved;
            }
        }

        private void StartSegment(
            long sequence,
            CanonicalPacketHeader header,
            byte lapNumber,
            bool isLeading)
        {
            if (lapNumber == 0)
            {
                throw MalformedOrder();
            }

            var flags = LapEvidenceFlags.None;
            if (_currentContext is null)
            {
                flags |= LapEvidenceFlags.MissingContext;
            }
            else
            {
                if (!_currentContext.IsSupported)
                {
                    flags |= LapEvidenceFlags.UnsupportedContext;
                }

                if (_currentContext.PlayerCarIndex != header.PlayerCarIndex
                    || _currentContext.SecondaryPlayerCarIndex
                    != header.SecondaryPlayerCarIndex)
                {
                    flags |= LapEvidenceFlags.PlayerIndexChanged;
                }
            }

            _segment = new(
                sequence,
                lapNumber,
                header.PlayerCarIndex,
                header.SecondaryPlayerCarIndex,
                _currentContext,
                flags,
                isLeading);
        }

        private void MarkPlayerChange(CanonicalPacketHeader header)
        {
            if (_segment is not null
                && (_segment.PlayerCarIndex != header.PlayerCarIndex
                    || _segment.SecondaryPlayerCarIndex
                    != header.SecondaryPlayerCarIndex))
            {
                _segment.Flags |= LapEvidenceFlags.PlayerIndexChanged;
            }
        }

        private void AddCompleteCandidate(
            long completionEvidenceSequence,
            uint officialLapTimeMilliseconds)
        {
            var segment = _segment!;
            var boundary = LapBoundary.Complete(
                segment.StartSourceSequence,
                completionEvidenceSequence,
                segment.LapNumber,
                officialLapTimeMilliseconds);
            AddCandidate(segment, boundary);
        }

        private void AddPartialCandidate(
            LapBoundaryCompleteness completeness,
            long endSourceSequenceExclusive)
        {
            var segment = _segment!;
            var boundary = LapBoundary.Partial(
                completeness,
                segment.StartSourceSequence,
                endSourceSequenceExclusive,
                segment.LapNumber);
            AddCandidate(segment, boundary);
        }

        private void AddCandidate(SegmentState segment, LapBoundary boundary)
        {
            if (_candidates.Count >= BahrainLapAuditContract.MaximumInventoryCandidates)
            {
                throw new BahrainLapAssemblyException(
                    BahrainLapAssemblyFailureKind.CandidateLimitExceeded,
                    "The provisional lap inventory exceeded its supported bound.");
            }

            var flags = LapEvidence.Validate(segment.Flags);
            var id = BahrainLapCandidateIdentity.Calculate(
                _completion.Identity.IdentitySha256,
                boundary,
                flags,
                segment.Context);
            _candidates.Add(new(id, boundary, flags, segment.Context));
        }

        private void ValidateAndAdvanceCoverage(CanonicalRecord record)
        {
            if (_sequenceExhausted)
            {
                throw MalformedOrder();
            }

            long finalSequence;
            switch (record.Kind)
            {
                case CanonicalRecordKind.Gap
                    when record.FirstMissingSequence == _nextExpectedSequence
                         && record.LastMissingSequence is { } gapLast:
                    finalSequence = gapLast;
                    break;
                case CanonicalRecordKind.Observation or CanonicalRecordKind.Exclusion
                    when record.SourceSequence == _nextExpectedSequence:
                    finalSequence = record.SourceSequence.Value;
                    AccountSourceSequence(finalSequence);
                    break;
                default:
                    throw MalformedOrder();
            }

            if (finalSequence == long.MaxValue)
            {
                _sequenceExhausted = true;
            }
            else
            {
                _nextExpectedSequence = finalSequence + 1;
            }
        }

        private void AccountSourceSequence(long sequence)
        {
            _firstSourceSequence ??= sequence;
            _lastSourceSequence = sequence;
        }

        private void ValidateCompletion()
        {
            if (_recordCount != _completion.RecordCount
                || _observationCount != _completion.ObservationCount
                || _exclusionCount != _completion.ExclusionCount
                || _gapCount != _completion.GapCount
                || _firstSourceSequence != _completion.FirstSourceSequence
                || _lastSourceSequence != _completion.LastSourceSequence)
            {
                throw new BahrainLapAssemblyException(
                    BahrainLapAssemblyFailureKind.CompletionMismatch,
                    "Canonical replay accounting does not match its verified completion.");
            }
        }

        private static BahrainLapAssemblyException MalformedOrder() =>
            new(
                BahrainLapAssemblyFailureKind.MalformedCanonicalOrder,
                "Canonical lap input must cover source order exactly.");
    }

    private sealed class SegmentState
    {
        public SegmentState(
            long startSourceSequence,
            byte lapNumber,
            byte playerCarIndex,
            byte secondaryPlayerCarIndex,
            BahrainTelemetryContext? context,
            LapEvidenceFlags flags,
            bool isLeading)
        {
            StartSourceSequence = startSourceSequence;
            LapNumber = lapNumber;
            PlayerCarIndex = playerCarIndex;
            SecondaryPlayerCarIndex = secondaryPlayerCarIndex;
            Context = context;
            Flags = flags;
            IsLeading = isLeading;
        }

        public long StartSourceSequence { get; }
        public byte LapNumber { get; }
        public byte PlayerCarIndex { get; }
        public byte SecondaryPlayerCarIndex { get; }
        public BahrainTelemetryContext? Context { get; }
        public bool IsLeading { get; }
        public LapEvidenceFlags Flags { get; set; }
    }
}
