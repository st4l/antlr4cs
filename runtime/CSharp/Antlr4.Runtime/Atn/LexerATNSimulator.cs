/*
 * [The "BSD license"]
 *  Copyright (c) 2013 Terence Parr
 *  Copyright (c) 2013 Sam Harwell
 *  All rights reserved.
 *
 *  Redistribution and use in source and binary forms, with or without
 *  modification, are permitted provided that the following conditions
 *  are met:
 *
 *  1. Redistributions of source code must retain the above copyright
 *     notice, this list of conditions and the following disclaimer.
 *  2. Redistributions in binary form must reproduce the above copyright
 *     notice, this list of conditions and the following disclaimer in the
 *     documentation and/or other materials provided with the distribution.
 *  3. The name of the author may not be used to endorse or promote products
 *     derived from this software without specific prior written permission.
 *
 *  THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
 *  IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
 *  OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
 *  IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT,
 *  INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 *  NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 *  DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 *  THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 *  (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
 *  THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
using System;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Dfa;
using Antlr4.Runtime.Misc;
using Sharpen;

namespace Antlr4.Runtime.Atn
{
    /// <summary>"dup" of ParserInterpreter</summary>
    public class LexerATNSimulator : ATNSimulator
    {
#if !PORTABLE
        public const bool debug = false;

        public const bool dfa_debug = false;
#endif

        public const int MinDfaEdge = 0;

        public const int MaxDfaEdge = 127;

        public bool optimize_tail_calls = true;

        /// <summary>
        /// When we hit an accept state in either the DFA or the ATN, we
        /// have to notify the character stream to start buffering characters
        /// via
        /// <see cref="Antlr4.Runtime.IIntStream.Mark()">Antlr4.Runtime.IIntStream.Mark()</see>
        /// and record the current state. The current sim state
        /// includes the current index into the input, the current line,
        /// and current character position in that line. Note that the Lexer is
        /// tracking the starting line and characterization of the token. These
        /// variables track the "state" of the simulator when it hits an accept state.
        /// <p/>
        /// We track these variables separately for the DFA and ATN simulation
        /// because the DFA simulation often has to fail over to the ATN
        /// simulation. If the ATN simulation fails, we need the DFA to fall
        /// back to its previously accepted state, if any. If the ATN succeeds,
        /// then the ATN does the accept and the DFA simulator that invoked it
        /// can simply return the predicted token type.
        /// </summary>
        protected internal class SimState
        {
            protected internal int index = -1;

            protected internal int line = 0;

            protected internal int charPos = -1;

            protected internal DFAState dfaState;

            // forces unicode to stay in ATN
            protected internal virtual void Reset()
            {
                index = -1;
                line = 0;
                charPos = -1;
                dfaState = null;
            }
        }

        [Nullable]
        protected internal readonly Lexer recog;

        /// <summary>The current token's starting index into the character stream.</summary>
        /// <remarks>
        /// The current token's starting index into the character stream.
        /// Shared across DFA to ATN simulation in case the ATN fails and the
        /// DFA did not have a previous accept state. In this case, we use the
        /// ATN-generated exception object.
        /// </remarks>
        protected internal int startIndex = -1;

        /// <summary>line number 1..n within the input</summary>
        protected internal int line = 1;

        /// <summary>The index of the character relative to the beginning of the line 0..n-1</summary>
        protected internal int charPositionInLine = 0;

        protected internal int mode = Lexer.DefaultMode;

        /// <summary>Used during DFA/ATN exec to record the most recent accept configuration info
        ///     </summary>
        [NotNull]
        protected internal readonly LexerATNSimulator.SimState prevAccept = new LexerATNSimulator.SimState
            ();

        public static int match_calls = 0;

        public LexerATNSimulator(ATN atn) : this(null, atn)
        {
        }

        public LexerATNSimulator(Lexer recog, ATN atn) : base(atn)
        {
            this.recog = recog;
        }

        public virtual void CopyState(LexerATNSimulator simulator)
        {
            this.charPositionInLine = simulator.charPositionInLine;
            this.line = simulator.line;
            this.mode = simulator.mode;
            this.startIndex = simulator.startIndex;
        }

        public virtual int Match(ICharStream input, int mode)
        {
            match_calls++;
            this.mode = mode;
            int mark = input.Mark();
            try
            {
                this.startIndex = input.Index;
                this.prevAccept.Reset();
                DFAState s0 = atn.modeToDFA[mode].s0.Get();
                if (s0 == null)
                {
                    return MatchATN(input);
                }
                else
                {
                    return ExecATN(input, s0);
                }
            }
            finally
            {
                input.Release(mark);
            }
        }

        public override void Reset()
        {
            prevAccept.Reset();
            startIndex = -1;
            line = 1;
            charPositionInLine = 0;
            mode = Lexer.DefaultMode;
        }

        // only called from test code from outside
        protected internal virtual int MatchATN(ICharStream input)
        {
            ATNState startState = atn.modeToStartState[mode];
            int old_mode = mode;
            ATNConfigSet s0_closure = ComputeStartState(input, startState);
            bool suppressEdge = s0_closure.HasSemanticContext;
            if (suppressEdge)
            {
                s0_closure.ClearExplicitSemanticContext();
            }
            DFAState next = AddDFAState(s0_closure);
            if (!suppressEdge)
            {
                if (!atn.modeToDFA[mode].s0.CompareAndSet(null, next))
                {
                    next = atn.modeToDFA[mode].s0.Get();
                }
            }
            int predict = ExecATN(input, next);
            return predict;
        }

        protected internal virtual int ExecATN(ICharStream input, DFAState ds0)
        {
            //System.out.println("enter exec index "+input.index()+" from "+ds0.configs);
            int t = input.La(1);
            DFAState s = ds0;
            // s is current/from DFA state
            while (true)
            {
                // while more work
                // As we move src->trg, src->trg, we keep track of the previous trg to
                // avoid looking up the DFA state again, which is expensive.
                // If the previous target was already part of the DFA, we might
                // be able to avoid doing a reach operation upon t. If s!=null,
                // it means that semantic predicates didn't prevent us from
                // creating a DFA state. Once we know s!=null, we check to see if
                // the DFA state has an edge already for t. If so, we can just reuse
                // it's configuration set; there's no point in re-computing it.
                // This is kind of like doing DFA simulation within the ATN
                // simulation because DFA simulation is really just a way to avoid
                // computing reach/closure sets. Technically, once we know that
                // we have a previously added DFA state, we could jump over to
                // the DFA simulator. But, that would mean popping back and forth
                // a lot and making things more complicated algorithmically.
                // This optimization makes a lot of sense for loops within DFA.
                // A character will take us back to an existing DFA state
                // that already has lots of edges out of it. e.g., .* in comments.
                ATNConfigSet closure = s.configs;
                DFAState target = null;
                target = s.GetTarget(t);
                if (target == Error)
                {
                    break;
                }
#if !PORTABLE
                if (debug && target != null)
                {
                    System.Console.Out.WriteLine("reuse state " + s.stateNumber + " edge to " + target
                        .stateNumber);
                }
#endif
                if (target == null)
                {
                    ATNConfigSet reach = new OrderedATNConfigSet();
                    // if we don't find an existing DFA state
                    // Fill reach starting from closure, following t transitions
                    GetReachableConfigSet(input, closure, reach, t);
                    if (reach.Count == 0)
                    {
                        // we got nowhere on t from s
                        // we reached state associated with closure for sure, so
                        // make sure it's defined. worst case, we define s0 from
                        // start state configs.
                        DFAState from = s != null ? s : AddDFAState(closure);
                        // we got nowhere on t, don't throw out this knowledge; it'd
                        // cause a failover from DFA later.
                        AddDFAEdge(from, t, Error);
                        break;
                    }
                    // stop when we can't match any more char
                    // Add an edge from s to target DFA found/created for reach
                    target = AddDFAEdge(s, t, reach);
                }
                if (target.isAcceptState)
                {
                    CaptureSimState(prevAccept, input, target);
                    if (t == IntStreamConstants.Eof)
                    {
                        break;
                    }
                }
                if (t != IntStreamConstants.Eof)
                {
                    Consume(input);
                    t = input.La(1);
                }
                s = target;
            }
            // flip; current DFA target becomes new src/from state
            return FailOrAccept(prevAccept, input, s.configs, t);
        }

        protected internal virtual int FailOrAccept(LexerATNSimulator.SimState prevAccept
            , ICharStream input, ATNConfigSet reach, int t)
        {
            if (prevAccept.dfaState != null)
            {
                int ruleIndex = prevAccept.dfaState.lexerRuleIndex;
                int actionIndex = prevAccept.dfaState.lexerActionIndex;
                Accept(input, ruleIndex, actionIndex, prevAccept.index, prevAccept.line, prevAccept
                    .charPos);
                return prevAccept.dfaState.prediction;
            }
            else
            {
                // if no accept and EOF is first char, return EOF
                if (t == IntStreamConstants.Eof && input.Index == startIndex)
                {
                    return TokenConstants.Eof;
                }
                throw new LexerNoViableAltException(recog, input, startIndex, reach);
            }
        }

        /// <summary>
        /// Given a starting configuration set, figure out all ATN configurations
        /// we can reach upon input
        /// <code>t</code>
        /// . Parameter
        /// <code>reach</code>
        /// is a return
        /// parameter.
        /// </summary>
        protected internal virtual void GetReachableConfigSet(ICharStream input, ATNConfigSet
             closure, ATNConfigSet reach, int t)
        {
            // this is used to skip processing for configs which have a lower priority
            // than a config that already reached an accept state for the same rule
            int skipAlt = ATN.InvalidAltNumber;
            foreach (ATNConfig c in closure)
            {
                if (c.Alt == skipAlt)
                {
                    continue;
                }
                int n = c.State.NumberOfOptimizedTransitions;
                for (int ti = 0; ti < n; ti++)
                {
                    // for each optimized transition
                    Transition trans = c.State.GetOptimizedTransition(ti);
                    ATNState target = GetReachableTarget(trans, t);
                    if (target != null)
                    {
                        if (Closure(input, c.Transform(target), reach, true))
                        {
                            // any remaining configs for this alt have a lower priority than
                            // the one that just reached an accept state.
                            skipAlt = c.Alt;
                            break;
                        }
                    }
                }
            }
        }

        protected internal virtual void Accept(ICharStream input, int ruleIndex, int actionIndex
            , int index, int line, int charPos)
        {
            if (actionIndex >= 0 && recog != null)
            {
                recog.Action(null, ruleIndex, actionIndex);
            }
            // seek to after last char in token
            input.Seek(index);
            this.line = line;
            this.charPositionInLine = charPos;
            if (input.La(1) != IntStreamConstants.Eof)
            {
                Consume(input);
            }
        }

        [return: Nullable]
        public virtual ATNState GetReachableTarget(Transition trans, int t)
        {
            if (trans.Matches(t, Lexer.MinCharValue, Lexer.MaxCharValue))
            {
                return trans.target;
            }
            return null;
        }

        [return: NotNull]
        protected internal virtual ATNConfigSet ComputeStartState(ICharStream input, ATNState
             p)
        {
            PredictionContext initialContext = PredictionContext.EmptyFull;
            ATNConfigSet configs = new OrderedATNConfigSet();
            for (int i = 0; i < p.NumberOfTransitions; i++)
            {
                ATNState target = p.Transition(i).target;
                ATNConfig c = ATNConfig.Create(target, i + 1, initialContext);
                Closure(input, c, configs, false);
            }
            return configs;
        }

        /// <summary>
        /// Since the alternatives within any lexer decision are ordered by
        /// preference, this method stops pursuing the closure as soon as an accept
        /// state is reached.
        /// </summary>
        /// <remarks>
        /// Since the alternatives within any lexer decision are ordered by
        /// preference, this method stops pursuing the closure as soon as an accept
        /// state is reached. After the first accept state is reached by depth-first
        /// search from
        /// <code>config</code>
        /// , all other (potentially reachable) states for
        /// this rule would have a lower priority.
        /// </remarks>
        /// <returns>
        /// 
        /// <code>true</code>
        /// if an accept state is reached, otherwise
        /// <code>false</code>
        /// .
        /// </returns>
        protected internal virtual bool Closure(ICharStream input, ATNConfig config, ATNConfigSet
             configs, bool speculative)
        {
            if (config.State is RuleStopState)
            {
                PredictionContext context = config.Context;
                if (context.IsEmpty)
                {
                    configs.AddItem(config);
                    return true;
                }
                else
                {
                    if (context.HasEmpty)
                    {
                        configs.AddItem(config.Transform(config.State, PredictionContext.EmptyFull));
                        return true;
                    }
                }
                for (int i = 0; i < context.Size; i++)
                {
                    int returnStateNumber = context.GetReturnState(i);
                    if (returnStateNumber == PredictionContext.EmptyFullStateKey)
                    {
                        continue;
                    }
                    PredictionContext newContext = context.GetParent(i);
                    // "pop" return state
                    ATNState returnState = atn.states[returnStateNumber];
                    ATNConfig c = ATNConfig.Create(returnState, config.Alt, newContext);
                    if (Closure(input, c, configs, speculative))
                    {
                        return true;
                    }
                }
                return false;
            }
            // optimization
            if (!config.State.OnlyHasEpsilonTransitions)
            {
                configs.AddItem(config);
            }
            ATNState p = config.State;
            for (int i_1 = 0; i_1 < p.NumberOfOptimizedTransitions; i_1++)
            {
                Transition t = p.GetOptimizedTransition(i_1);
                ATNConfig c = GetEpsilonTarget(input, config, t, configs, speculative);
                if (c != null)
                {
                    if (Closure(input, c, configs, speculative))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // side-effect: can alter configs.hasSemanticContext
        [return: Nullable]
        public virtual ATNConfig GetEpsilonTarget(ICharStream input, ATNConfig config, Transition
             t, ATNConfigSet configs, bool speculative)
        {
            ATNConfig c;
            switch (t.TransitionType)
            {
                case TransitionType.Rule:
                {
                    RuleTransition ruleTransition = (RuleTransition)t;
                    if (optimize_tail_calls && ruleTransition.optimizedTailCall && !config.Context.HasEmpty)
                    {
                        c = config.Transform(t.target);
                    }
                    else
                    {
                        PredictionContext newContext = config.Context.GetChild(ruleTransition.followState
                            .stateNumber);
                        c = config.Transform(t.target, newContext);
                    }
                    break;
                }

                case TransitionType.Precedence:
                {
                    throw new NotSupportedException("Precedence predicates are not supported in lexers."
                        );
                }

                case TransitionType.Predicate:
                {
                    PredicateTransition pt = (PredicateTransition)t;
                    configs.MarkExplicitSemanticContext();
                    if (EvaluatePredicate(input, pt.ruleIndex, pt.predIndex, speculative))
                    {
                        c = config.Transform(t.target);
                    }
                    else
                    {
                        c = null;
                    }
                    break;
                }

                case TransitionType.Action:
                {
                    // ignore actions; just exec one per rule upon accept
                    c = config.Transform(t.target, ((ActionTransition)t).actionIndex);
                    break;
                }

                case TransitionType.Epsilon:
                {
                    c = config.Transform(t.target);
                    break;
                }

                default:
                {
                    c = null;
                    break;
                }
            }
            return c;
        }

        /// <summary>Evaluate a predicate specified in the lexer.</summary>
        /// <remarks>
        /// Evaluate a predicate specified in the lexer.
        /// <p/>
        /// If
        /// <code>speculative</code>
        /// is
        /// <code>true</code>
        /// , this method was called before
        /// <see cref="Consume(Antlr4.Runtime.ICharStream)">Consume(Antlr4.Runtime.ICharStream)
        ///     </see>
        /// for the matched character. This method should call
        /// <see cref="Consume(Antlr4.Runtime.ICharStream)">Consume(Antlr4.Runtime.ICharStream)
        ///     </see>
        /// before evaluating the predicate to ensure position
        /// sensitive values, including
        /// <see cref="Antlr4.Runtime.Lexer.Text()">Antlr4.Runtime.Lexer.Text()</see>
        /// ,
        /// <see cref="Antlr4.Runtime.Lexer.Line()">Antlr4.Runtime.Lexer.Line()</see>
        /// ,
        /// and
        /// <see cref="Antlr4.Runtime.Lexer.Column()">Antlr4.Runtime.Lexer.Column()</see>
        /// , properly reflect the current
        /// lexer state. This method should restore
        /// <code>input</code>
        /// and the simulator
        /// to the original state before returning (i.e. undo the actions made by the
        /// call to
        /// <see cref="Consume(Antlr4.Runtime.ICharStream)">Consume(Antlr4.Runtime.ICharStream)
        ///     </see>
        /// .
        /// </remarks>
        /// <param name="input">The input stream.</param>
        /// <param name="ruleIndex">The rule containing the predicate.</param>
        /// <param name="predIndex">The index of the predicate within the rule.</param>
        /// <param name="speculative">
        /// 
        /// <code>true</code>
        /// if the current index in
        /// <code>input</code>
        /// is
        /// one character before the predicate's location.
        /// </param>
        /// <returns>
        /// 
        /// <code>true</code>
        /// if the specified predicate evaluates to
        /// <code>true</code>
        /// .
        /// </returns>
        protected internal virtual bool EvaluatePredicate(ICharStream input, int ruleIndex
            , int predIndex, bool speculative)
        {
            // assume true if no recognizer was provided
            if (recog == null)
            {
                return true;
            }
            if (!speculative)
            {
                return recog.Sempred(null, ruleIndex, predIndex);
            }
            int savedCharPositionInLine = charPositionInLine;
            int savedLine = line;
            int index = input.Index;
            int marker = input.Mark();
            try
            {
                Consume(input);
                return recog.Sempred(null, ruleIndex, predIndex);
            }
            finally
            {
                charPositionInLine = savedCharPositionInLine;
                line = savedLine;
                input.Seek(index);
                input.Release(marker);
            }
        }

        protected internal virtual void CaptureSimState(LexerATNSimulator.SimState settings
            , ICharStream input, DFAState dfaState)
        {
            settings.index = input.Index;
            settings.line = line;
            settings.charPos = charPositionInLine;
            settings.dfaState = dfaState;
        }

        [return: NotNull]
        protected internal virtual DFAState AddDFAEdge(DFAState from, int t, ATNConfigSet
             q)
        {
            bool suppressEdge = q.HasSemanticContext;
            if (suppressEdge)
            {
                q.ClearExplicitSemanticContext();
            }
            DFAState to = AddDFAState(q);
            if (suppressEdge)
            {
                return to;
            }
            AddDFAEdge(from, t, to);
            return to;
        }

        protected internal virtual void AddDFAEdge(DFAState p, int t, DFAState q)
        {
            if (p != null)
            {
                p.SetTarget(t, q);
            }
        }

        /// <summary>
        /// Add a new DFA state if there isn't one with this set of
        /// configurations already.
        /// </summary>
        /// <remarks>
        /// Add a new DFA state if there isn't one with this set of
        /// configurations already. This method also detects the first
        /// configuration containing an ATN rule stop state. Later, when
        /// traversing the DFA, we will know which rule to accept.
        /// </remarks>
        [return: NotNull]
        protected internal virtual DFAState AddDFAState(ATNConfigSet configs)
        {
            System.Diagnostics.Debug.Assert(!configs.HasSemanticContext);
            DFAState proposed = new DFAState(configs, 0, MaxDfaEdge);
            DFAState existing;
            if (atn.modeToDFA[mode].states.TryGetValue(proposed, out existing))
            {
                return existing;
            }
            configs.OptimizeConfigs(this);
            DFAState newState = new DFAState(configs.Clone(true), 0, MaxDfaEdge);
            ATNConfig firstConfigWithRuleStopState = null;
            foreach (ATNConfig c in configs)
            {
                if (c.State is RuleStopState)
                {
                    firstConfigWithRuleStopState = c;
                    break;
                }
            }
            if (firstConfigWithRuleStopState != null)
            {
                newState.isAcceptState = true;
                newState.lexerRuleIndex = firstConfigWithRuleStopState.State.ruleIndex;
                newState.lexerActionIndex = firstConfigWithRuleStopState.ActionIndex;
                newState.prediction = atn.ruleToTokenType[newState.lexerRuleIndex];
            }
            return atn.modeToDFA[mode].AddState(newState);
        }

        [return: NotNull]
        public DFA GetDFA(int mode)
        {
            return atn.modeToDFA[mode];
        }

        /// <summary>Get the text matched so far for the current token.</summary>
        /// <remarks>Get the text matched so far for the current token.</remarks>
        [return: NotNull]
        public virtual string GetText(ICharStream input)
        {
            // index is first lookahead char, don't include.
            return input.GetText(Interval.Of(startIndex, input.Index - 1));
        }

        public virtual int GetLine()
        {
            return line;
        }

        public virtual void SetLine(int line)
        {
            this.line = line;
        }

        public virtual int GetCharPositionInLine()
        {
            return charPositionInLine;
        }

        public virtual void SetCharPositionInLine(int charPositionInLine)
        {
            this.charPositionInLine = charPositionInLine;
        }

        public virtual void Consume(ICharStream input)
        {
            int curChar = input.La(1);
            if (curChar == '\n')
            {
                line++;
                charPositionInLine = 0;
            }
            else
            {
                charPositionInLine++;
            }
            input.Consume();
        }

        [return: NotNull]
        public virtual string GetTokenName(int t)
        {
            if (t == -1)
            {
                return "EOF";
            }
            //if ( atn.g!=null ) return atn.g.getTokenDisplayName(t);
            return "'" + (char)t + "'";
        }
    }
}
