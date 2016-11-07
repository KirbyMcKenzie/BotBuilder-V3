﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using Microsoft.Bot.Builder.Internals.Fibers;
using Microsoft.Bot.Connector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Bot.Builder.Internals.Scorables
{
    [Serializable]
    public abstract class AttributeString : Attribute, IEquatable<AttributeString>
    {
        protected abstract string Text { get; }
        bool IEquatable<AttributeString>.Equals(AttributeString other)
        {
            return other != null
                && object.Equals(this.Text, other.Text);
        }
        public override bool Equals(object other)
        {
            return base.Equals(other as AttributeString);
        }
        public override int GetHashCode()
        {
            return this.Text.GetHashCode();
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    [Serializable]
    public sealed class RegexPatternAttribute : AttributeString
    {
        public readonly string Pattern;
        public RegexPatternAttribute(string pattern)
        {
            SetField.NotNull(out this.Pattern, nameof(pattern), pattern);
        }

        protected override string Text
        {
            get
            {
                return this.Pattern;
            }
        }
    }

    public sealed class RegexMatchScorableFactory : IScorableFactory<IResolver, Match>
    {
        private readonly Func<string, Regex> make;
        public RegexMatchScorableFactory(Func<string, Regex> make)
        {
            SetField.NotNull(out this.make, nameof(make), make);
        }

        IScorable<IResolver, Match> IScorableFactory<IResolver, Match>.ScorableFor(IEnumerable<MethodInfo> methods)
        {
            var specs =
                from method in methods
                from pattern in InheritedAttributes.For<RegexPatternAttribute>(method)
                select new { method, pattern };

            var scorableByMethod = methods.ToDictionary(m => m, m => new MethodScorable(m));

            // for a given regular expression pattern, fold the corresponding method scorables together to enable overload resolution
            var scorables =
                from spec in specs
                group spec by spec.pattern into patterns
                let method = patterns.Select(m => scorableByMethod[m.method]).ToArray().Fold(Binding.ResolutionComparer.Instance)
                let regex = this.make(patterns.Key.Pattern)
                select new RegexMatchScorable<Binding, Binding>(regex, method);

            var all = scorables.ToArray().Fold(MatchComparer.Instance);

            return all;
        }
    }

    /// <summary>
    /// Static helper methods for RegexMatchScorable.
    /// </summary>
    public static partial class RegexMatchScorable
    {
        /// <summary>
        /// Calculate a normalized 0-1 score for a regular expression match.
        /// </summary>
        /// <remarks>
        /// This implementation assumes that the entire input string is matched by the regular expression
        /// so that group 0 is the entire input string and the other groups are the significant portions of
        /// that entire input string.
        /// </remarks>
        public static double ScoreFor(Match match)
        {
            var groups = match.Groups;
            var numerator = 0;
            for (int index = 1; index < groups.Count; ++index)
            {
                var group = groups[index];
                numerator += group.Length;
            }
            var denominator = groups[0].Length;
            var score = ((double)numerator) / denominator;
            return score;
        }
    }

    /// <summary>
    /// Scorable to represent a regular expression match against an activity's text.
    /// </summary>
    public sealed class RegexMatchScorable<InnerState, InnerScore> : ResolverScorable<RegexMatchScorable<InnerState, InnerScore>.Scope, Match, InnerState, InnerScore>
    {
        private readonly Regex regex;
        public sealed class Scope : ResolverScope<InnerScore>
        {
            public readonly Regex Regex;
            public readonly Match Match;
            public Scope(Regex regex, Match match, IResolver inner)
                : base(inner)
            {
                SetField.NotNull(out this.Regex, nameof(regex), regex);
                SetField.NotNull(out this.Match, nameof(match), match);
            }
            public override bool TryResolve(Type type, object tag, out object value)
            {
                var name = tag as string;
                if (name != null)
                {
                    var capture = this.Match.Groups[name];
                    if (capture != null && capture.Success)
                    {
                        if (type.IsAssignableFrom(typeof(Capture)))
                        {
                            value = capture;
                            return true;
                        }
                        else if (type.IsAssignableFrom(typeof(string)))
                        {
                            value = capture.Value;
                            return true;
                        }
                    }
                }

                if (type.IsAssignableFrom(typeof(Regex)))
                {
                    value = this.Regex;
                    return true;
                }

                if (type.IsAssignableFrom(typeof(Match)))
                {
                    value = this.Match;
                    return true;
                }

                var captures = this.Match.Captures;
                if (type.IsAssignableFrom(typeof(CaptureCollection)))
                {
                    value = captures;
                    return true;
                }

                // i.e. for IActivity
                return base.TryResolve(type, tag, out value);
            }
        }

        public RegexMatchScorable(Regex regex, IScorable<IResolver, InnerScore> inner)
            : base(inner)
        {
            SetField.NotNull(out this.regex, nameof(regex), regex);
        }
        protected override async Task<Scope> PrepareAsync(IResolver resolver, CancellationToken token)
        {
            IMessageActivity message;
            if (!resolver.TryResolve(null, out message))
            {
                return null;
            }

            var text = message.Text;
            if (text == null)
            {
                return null;
            }

            var match = this.regex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var scope = new Scope(this.regex, match, resolver);
            scope.Scorable = this.inner;
            scope.State = await this.inner.PrepareAsync(scope, token);
            return scope;
        }
        protected override Match GetScore(IResolver resolver, Scope state)
        {
            return state.Match;
        }
    }

    public sealed class MatchComparer : IComparer<Match>
    {
        public static readonly IComparer<Match> Instance = new MatchComparer();
        private MatchComparer()
        {
        }
        int IComparer<Match>.Compare(Match one, Match two)
        {
            Func<Match, Pair<bool, int>> PairFor = match => Pair.Create
            (
                ! match.Success,
                -match.Value.Length
            );

            var pairOne = PairFor(one);
            var pairTwo = PairFor(two);
            return pairOne.CompareTo(pairTwo);
        }
    }
}
