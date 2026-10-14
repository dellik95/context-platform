using System.Text;
using System.Text.RegularExpressions;
using Context.Domain.Enums;
using Context.Memory.Extraction;

namespace Context.Memory.Consolidation;

/// <summary>
/// One LLM call per related group during batch reconsolidation: the model rewrites the group into
/// canonical memories and names the sources that contradict each other or belong elsewhere. Parsing
/// is strict about indexes so a sloppy answer can only make the group ambiguous, never merge wrongly.
/// </summary>
internal static partial class ReconsolidationPrompt
{
    public const string SystemPrompt =
        """
        You reconsolidate the long-term memory of a code repository. You receive a numbered group of
        knowledge memories that share files, symbols, tickets or a topic, and may describe the same
        subjects. Rewrite them into as few canonical memories as the subjects allow.

        Rules:
        - Merge sources into one canonical only when they are about the same specific subject (the same
          component, behavior, setting or procedure). Sharing a file, ticket or topic is not enough.
        - Copy every fact of every merged source verbatim into the canonical's FACTS. Never drop,
          shorten or reword a fact; near-identical facts from different sources may be written once.
        - A canonical states how things are now, never what a session did.
        - Two sources that make incompatible claims about the same thing go in CONTRADICTIONS and are
          not merged.
        - A source about a different subject from all the others goes in KEEP_SEPARATE.
        - Every source number appears in exactly one of: one canonical's SOURCES, CONTRADICTIONS,
          KEEP_SEPARATE.

        Answer in exactly this format and nothing else:

        CANONICAL
        TYPE: <one of Fact, Decision, Constraint, Architecture, Behavior, Configuration, Discovery, Procedure, Issue>
        TITLE: <one line>
        TOPIC: <2-5 word subsystem phrase>
        BODY:
        <the merged explanation>
        FACTS:
        - <fact>
        ENTITIES: <comma-separated identifiers, files, tickets and config keys>
        FILES_READ:
        - <path>
        FILES_MODIFIED:
        - <path>
        SOURCES: <comma-separated source numbers>
        END_CANONICAL

        CONTRADICTIONS:
        - <source number>|<source number>|<why both cannot be true>

        KEEP_SEPARATE: <comma-separated source numbers>

        Write "CONTRADICTIONS: none" and "KEEP_SEPARATE: none" when there are none.
        """;

    public static string BuildUserPrompt(IReadOnlyList<Context.Domain.Entities.Memory> sources, int excerptChars)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (i > 0)
            {
                sb.AppendLine();
            }

            sb.Append("SOURCE ").Append(i + 1).AppendLine();
            sb.Append("TYPE: ").AppendLine(source.Type.ToString());
            sb.Append("TITLE: ").AppendLine(source.Title);
            if (!string.IsNullOrWhiteSpace(source.Topic))
            {
                sb.Append("TOPIC: ").AppendLine(source.Topic);
            }

            var facts = EntityExtractor.ParseList(source.FactsJson);
            if (facts.Count > 0)
            {
                sb.AppendLine("FACTS:");
                foreach (var fact in facts)
                {
                    sb.Append("- ").AppendLine(fact);
                }
            }

            var files = EntityExtractor.ParseList(source.FilesModifiedJson).Concat(EntityExtractor.ParseList(source.FilesReadJson)).ToList();
            if (files.Count > 0)
            {
                sb.Append("FILES: ").AppendLine(string.Join(", ", files));
            }

            var body = source.Body.Trim();
            sb.Append("BODY: ").AppendLine(body.Length <= excerptChars ? body : string.Concat(body.AsSpan(0, excerptChars), "…"));
        }

        return sb.ToString();
    }

    /// <summary>Everything the response says, with 1-based source numbers as written; Problems lists
    /// what makes the answer unusable as a plan (malformed canonicals, bad numbers, no sections).</summary>
    public static ReconsolidationResponse Parse(string? response, int sourceCount)
    {
        var canonicals = new List<ParsedCanonical>();
        var contradictions = new List<ParsedContradiction>();
        var keepSeparate = new List<int>();
        var problems = new List<string>();
        var sawSection = false;

        StringBuilder? block = null;
        List<int>? blockSources = null;
        var section = Section.None;

        void FinishBlock()
        {
            if (block is null)
            {
                return;
            }

            var number = canonicals.Count + 1;
            var parsed = MemoryCandidateParser.Parse(block.ToString(), EvidenceSource.Consolidation);
            if (parsed.Candidates.Count != 1)
            {
                problems.Add($"canonical {number} is malformed");
            }
            else if (blockSources is not { Count: > 0 })
            {
                problems.Add($"canonical {number} names no sources");
            }
            else
            {
                canonicals.Add(new ParsedCanonical(parsed.Candidates[0], blockSources));
            }

            block = null;
            blockSources = null;
        }

        foreach (var rawLine in (response ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();

            if (CanonicalHeaderRegex().IsMatch(line))
            {
                FinishBlock();
                block = new StringBuilder();
                blockSources = [];
                section = Section.None;
                sawSection = true;
                continue;
            }

            if (line.Equals("END_CANONICAL", StringComparison.OrdinalIgnoreCase))
            {
                FinishBlock();
                continue;
            }

            var contradictionsMatch = ContradictionsHeaderRegex().Match(line);
            if (contradictionsMatch.Success)
            {
                FinishBlock();
                section = Section.Contradictions;
                sawSection = true;
                AddContradiction(contradictionsMatch.Groups[1].Value);
                continue;
            }

            var keepMatch = KeepSeparateHeaderRegex().Match(line);
            if (keepMatch.Success)
            {
                FinishBlock();
                section = Section.KeepSeparate;
                sawSection = true;
                keepSeparate.AddRange(Numbers(keepMatch.Groups[1].Value));
                continue;
            }

            if (block is not null)
            {
                var sourcesMatch = SourcesRegex().Match(line);
                if (sourcesMatch.Success)
                {
                    blockSources!.AddRange(Numbers(sourcesMatch.Groups[1].Value));
                }
                else
                {
                    block.AppendLine(rawLine);
                }

                continue;
            }

            if (section == Section.Contradictions)
            {
                AddContradiction(line);
            }
            else if (section == Section.KeepSeparate && (line.StartsWith('-') || line.StartsWith('*')))
            {
                keepSeparate.AddRange(Numbers(line));
            }
        }

        FinishBlock();

        if (!sawSection)
        {
            problems.Add("the response has no recognizable sections");
        }

        foreach (var number in canonicals.SelectMany(c => c.Sources).Concat(keepSeparate).Concat(contradictions.SelectMany(c => new[] { c.A, c.B })))
        {
            if (number < 1 || number > sourceCount)
            {
                problems.Add($"source {number} does not exist");
            }
        }

        foreach (var c in contradictions.Where(c => c.A == c.B))
        {
            problems.Add($"source {c.A} contradicts itself");
        }

        return new ReconsolidationResponse(canonicals, contradictions, [.. keepSeparate.Distinct()], [.. problems.Distinct()]);

        void AddContradiction(string text)
        {
            var match = ContradictionLineRegex().Match(text);
            if (match.Success)
            {
                var reason = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
                contradictions.Add(new ParsedContradiction(
                    int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), string.IsNullOrEmpty(reason) ? null : reason));
            }
        }
    }

    private static IEnumerable<int> Numbers(string text) =>
        NumberRegex().Matches(text).Select(m => int.TryParse(m.Value, out var n) ? n : -1).Where(n => n >= 0);

    private enum Section { None, Contradictions, KeepSeparate }

    [GeneratedRegex(@"^CANONICAL\s*\d*\s*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalHeaderRegex();

    [GeneratedRegex(@"^SOURCES:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourcesRegex();

    [GeneratedRegex(@"^CONTRADICTIONS:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContradictionsHeaderRegex();

    [GeneratedRegex(@"^KEEP_SEPARATE:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeepSeparateHeaderRegex();

    [GeneratedRegex(@"^[-*]?\s*\[?(\d+)\]?\s*\|\s*\[?(\d+)\]?\s*(?:\|(.*))?$")]
    private static partial Regex ContradictionLineRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();
}

internal sealed record ParsedCanonical(MemoryCandidate Candidate, IReadOnlyList<int> Sources);

internal sealed record ParsedContradiction(int A, int B, string? Reason);

internal sealed record ReconsolidationResponse(
    IReadOnlyList<ParsedCanonical> Canonicals,
    IReadOnlyList<ParsedContradiction> Contradictions,
    IReadOnlyList<int> KeepSeparate,
    IReadOnlyList<string> Problems);
