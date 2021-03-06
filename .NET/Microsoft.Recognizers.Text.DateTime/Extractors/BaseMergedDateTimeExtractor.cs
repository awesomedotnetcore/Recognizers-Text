﻿using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DateObject = System.DateTime;

using Microsoft.Recognizers.Text.Matcher;

namespace Microsoft.Recognizers.Text.DateTime
{
    public class BaseMergedDateTimeExtractor : IDateTimeExtractor
    {
        private readonly IMergedExtractorConfiguration config;

        public BaseMergedDateTimeExtractor(IMergedExtractorConfiguration config)
        {
            this.config = config;
        }

        public List<ExtractResult> Extract(string text)
        {
            return Extract(text, DateObject.Now);
        }

        public List<ExtractResult> Extract(string text, DateObject reference)
        {
            var ret = new List<ExtractResult>();

            var originText = text;
            List<MatchResult<string>> superfluousWordMatches = null;
            if ((this.config.Options & DateTimeOptions.EnablePreview) != 0)
            {
                text = MatchingUtil.PreProcessTextRemoveSuperfluousWords(text, this.config.SuperfluousWordMatcher, out superfluousWordMatches);
            }

            // The order is important, since there can be conflicts in merging
            AddTo(ret, this.config.DateExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.TimeExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.DatePeriodExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.DurationExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.DateTimeExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.TimePeriodExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.DateTimePeriodExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.SetExtractor.Extract(text, reference), text);
            AddTo(ret, this.config.HolidayExtractor.Extract(text, reference), text);

            if ((this.config.Options & DateTimeOptions.EnablePreview) != 0)
            {
                AddTo(ret, this.config.TimeZoneExtractor.Extract(text, reference), text);
                ret = this.config.TimeZoneExtractor.RemoveAmbiguousTimezone(ret);
            }

            // This should be at the end since if need the extractor to determine the previous text contains time or not
            AddTo(ret, NumberEndingRegexMatch(text, ret), text);

            // Modify time entity to an alternative DateTime expression if it follows a DateTime entity
            if ((this.config.Options & DateTimeOptions.ExtendedTypes) != 0)
            {
                ret = this.config.DateTimeAltExtractor.Extract(ret, text, reference);
            }

            ret = FilterUnspecificDatePeriod(ret);

            ret = FilterAmbiguity(ret, text);

            ret = AddMod(ret, text);

            // Filtering
            if ((this.config.Options & DateTimeOptions.CalendarMode) != 0)
            {
                ret = CheckCalendarFilterList(ret, text);
            }

            ret = ret.OrderBy(p => p.Start).ToList();

            if ((this.config.Options & DateTimeOptions.EnablePreview) != 0)
            {
                ret = MatchingUtil.PosProcessExtractionRecoverSuperfluousWords(ret, superfluousWordMatches, originText);
            }

            return ret;
        }

        private List<ExtractResult> CheckCalendarFilterList(List<ExtractResult> ers, string text)
        {
            foreach (var er in ers.Reverse<ExtractResult>())
            {
                foreach (var negRegex in this.config.FilterWordRegexList)
                {
                    var match = negRegex.Match(er.Text);
                    if (match.Success)
                    {
                        ers.Remove(er);
                    }
                }
            }

            return ers;
        }

        private void AddTo(List<ExtractResult> dst, List<ExtractResult> src, string text)
        {
            foreach (var result in src)
            {
                if ((config.Options & DateTimeOptions.SkipFromToMerge) != 0)
                {
                    if (ShouldSkipFromToMerge(result))
                    {
                        continue;
                    }
                }

                // @TODO: Is this really no longer necessary?
                //if (FilterAmbiguousSingleWord(result, text))
                //{
                //    continue;
                //}

                var isFound = false;
                var overlapIndexes = new List<int>();
                var firstIndex = -1;
                for (var i = 0; i < dst.Count; i++)
                {
                    if (dst[i].IsOverlap(result))
                    {
                        isFound = true;
                        if (dst[i].IsCover(result))
                        {
                            if (firstIndex == -1)
                            {
                                firstIndex = i;
                            }

                            overlapIndexes.Add(i);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                if (!isFound)
                {
                    dst.Add(result);
                }
                else if (overlapIndexes.Any())
                {
                    var tempDst = dst.Where((_, i) => !overlapIndexes.Contains(i)).ToList();

                    // Insert at the first overlap occurence to keep the order
                    tempDst.Insert(firstIndex, result);
                    dst.Clear();
                    dst.AddRange(tempDst);
                }
            }
        }

        private bool ShouldSkipFromToMerge(ExtractResult er) {
            return config.FromToRegex.IsMatch(er.Text);
        }

        private List<ExtractResult> FilterUnspecificDatePeriod(List<ExtractResult> ers)
        {
            ers.RemoveAll(o => this.config.UnspecificDatePeriodRegex.IsMatch(o.Text));
            return ers;
        }
        private List<ExtractResult> FilterAmbiguity(List<ExtractResult> ers, string text)
        {
            if (this.config.AmbiguityFiltersDict != null)
            {
                foreach (var regex in config.AmbiguityFiltersDict)
                {
                    if (regex.Key.IsMatch(text))
                    {
                        var matches = regex.Value.Matches(text).Cast<Match>();
                        ers = ers.Where(er =>
                                !matches.Any(m => m.Index < er.Start + er.Length && m.Index + m.Length > er.Start))
                            .ToList();
                    }
                }
            }
            return ers;
        }

        private bool FilterAmbiguousSingleWord(ExtractResult er, string text)
        {
            if (config.SingleAmbiguousMonthRegex.IsMatch(er.Text.ToLowerInvariant()))
            {
                var stringBefore = text.Substring(0, (int)er.Start).TrimEnd();
                if (!config.PrepositionSuffixRegex.IsMatch(stringBefore))
                {
                    return true;
                }
            }

            return false;
        }

        // Handle cases like "move 3pm appointment to 4"
        private List<ExtractResult> NumberEndingRegexMatch(string text, List<ExtractResult> extractResults)
        {
            var tokens = new List<Token>();

            foreach (var extractResult in extractResults)
            {
                if (extractResult.Type.Equals(Constants.SYS_DATETIME_TIME)
                    || extractResult.Type.Equals(Constants.SYS_DATETIME_DATETIME))
                {
                    var stringAfter = text.Substring((int)extractResult.Start + (int)extractResult.Length);
                    var match = this.config.NumberEndingPattern.Match(stringAfter);
                    if (match != null && match.Success)
                    {
                        var newTime = match.Groups["newTime"];
                        var numRes = this.config.IntegerExtractor.Extract(newTime.ToString());
                        if (numRes.Count == 0)
                        {
                            continue;
                        }

                        var startPosition = (int)extractResult.Start + (int)extractResult.Length + newTime.Index;
                        tokens.Add(new Token(startPosition, startPosition + newTime.Length));
                    }
                }
            }

            return Token.MergeAllTokens(tokens, text, Constants.SYS_DATETIME_TIME);
        }

        private List<ExtractResult> AddMod(List<ExtractResult> ers, string text)
        {
            foreach (var er in ers)
            {
                var success = TryMergeModifierToken(er, config.BeforeRegex, text);

                if (!success)
                {
                    success = TryMergeModifierToken(er, config.AfterRegex, text);
                }

                if (!success)
                {
                    success = TryMergeModifierToken(er, config.SinceRegex, text);
                }

                if (!success)
                {
                    TryMergeModifierToken(er, config.AroundRegex, text);
                }

                if (er.Type.Equals(Constants.SYS_DATETIME_DATEPERIOD))
                {
                    // 2012 or after/above
                    var afterStr = text.Substring((er.Start ?? 0) + (er.Length ?? 0)).ToLowerInvariant();

                    var match = config.YearAfterRegex.Match(afterStr.TrimStart());
                    if (match.Success && match.Index == 0 && match.Length == afterStr.Trim().Length)
                    {
                        var modLengh = match.Length + afterStr.IndexOf(match.Value);
                        er.Length += modLengh;
                        er.Text = text.Substring(er.Start ?? 0, er.Length ?? 0);
                    }
                }
            }

            return ers;
        }

        public bool TryMergeModifierToken(ExtractResult er, Regex tokenRegex, string text)
        {
            var beforeStr = text.Substring(0, er.Start ?? 0).ToLowerInvariant();
            if (HasTokenIndex(beforeStr.TrimEnd(), tokenRegex, out var tokenIndex))
            {
                var modLengh = beforeStr.Length - tokenIndex;
                er.Length += modLengh;
                er.Start -= modLengh;
                er.Text = text.Substring(er.Start ?? 0, er.Length ?? 0);
                return true;
            }

            return false;
        }

        public bool HasTokenIndex(string text, Regex regex, out int index)
        {
            index = -1;

            // Support cases has two or more specific tokens
            // For example, "show me sales after 2010 and before 2018 or before 2000"
            // When extract "before 2000", we need the second "before" which will be matched in the second Regex match

            var match = Regex.Match(text, regex.ToString(), RegexOptions.RightToLeft | RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success && string.IsNullOrEmpty(text.Substring(match.Index + match.Length)))
            {
                index = match.Index;
                return true;
            }

            return false;
        }
    }
}
