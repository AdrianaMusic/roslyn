﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// <para>
    /// A utility class for making a decision dag for a pattern-matching construct. A decision dag is represented by
    /// the class <see cref="BoundDecisionDag"/> and is a representation of a finite state automaton that performs a
    /// sequence of binary tests. Each node is represented by a <see cref="BoundDecisionDagNode"/>. There are four
    /// kind of nodes: <see cref="BoundTestDecisionDagNode"/> performs one of the binary tests;
    /// <see cref="BoundEvaluationDecisionDagNode"/> simply performs some computation and stores it in one or more
    /// temporary variables for use in subsequent nodes (think of it as a node with a single successor);
    /// <see cref="BoundWhenDecisionDagNode"/> represents the test performed by evaluating the expression of the
    /// when-clause of a switch case; and <see cref="BoundLeafDecisionDagNode"/> represents a leaf node when we
    /// have finally determined exactly which case matches. Each test processes a single input, and there are
    /// four kinds:<see cref="BoundDagNullTest"/> tests a value for null; <see cref="BoundDagNonNullTest"/>
    /// tests that a value is not null; <see cref="BoundDagTypeTest"/> checks if the value is of a given type;
    /// and <see cref="BoundDagValueTest"/> checks if the value is equal to a given constant. Of the evaluations,
    /// there are <see cref="BoundDagDeconstructEvaluation"/> which represents an invocation of a type's
    /// "Deconstruct" method; <see cref="BoundDagFieldEvaluation"/> reads a field; <see cref="BoundDagPropertyEvaluation"/>
    /// reads a property; and <see cref="BoundDagTypeEvaluation"/> converts a value from one type to another (which
    /// is performed only after testing that the value is of that type).
    /// </para>
    /// <para>
    /// In order to build this automaton, we start (in
    /// <see cref="MakeDecisionDag(ImmutableArray{DecisionDagBuilder.RemainingTestsForCase}, BoundLeafDecisionDagNode)"/>)
    /// by computing a description of the initial state in a <see cref="DagState"/>, and then
    /// for each such state description we decide what the test or evaluation will be at
    /// that state, and compute the successor state descriptions.
    /// A state description represented by a <see cref="DagState"/> is a collection of partially matched
    /// cases represented
    /// by <see cref="RemainingTestsForCase"/>, in which some number of the tests have already been performed
    /// for each case.
    /// When we have computed <see cref="DagState"/> descriptions for all of the states, we create a new
    /// <see cref="BoundDecisionDagNode"/> for each of them, containing
    /// the state transitions (including the test to perform at each node and the successor nodes) but
    /// not the state descriptions. A <see cref="BoundDecisionDag"/> containing this
    /// set of nodes becomes part of the bound nodes (e.g. in <see cref="BoundPatternSwitchStatement"/> and
    /// <see cref="BoundSwitchExpression"/>) and is used for semantic analysis and lowering.
    /// </para>
    /// </summary>
    internal class DecisionDagBuilder
    {
        private readonly CSharpCompilation _compilation;
        private readonly Conversions _conversions;
        private readonly TypeSymbol _booleanType;
        private readonly TypeSymbol _objectType;
        private readonly DiagnosticBag _diagnostics;
        private readonly LabelSymbol _defaultLabel;

        private DecisionDagBuilder(CSharpCompilation compilation, LabelSymbol defaultLabel, DiagnosticBag diagnostics)
        {
            this._compilation = compilation;
            this._conversions = compilation.Conversions;
            this._booleanType = compilation.GetSpecialType(SpecialType.System_Boolean);
            this._objectType = compilation.GetSpecialType(SpecialType.System_Object);
            _diagnostics = diagnostics;
            _defaultLabel = defaultLabel;
        }

        /// <summary>
        /// Create a decision dag for a switch statement.
        /// </summary>
        public static BoundDecisionDag CreateDecisionDagForSwitchStatement(
            CSharpCompilation compilation,
            SyntaxNode syntax,
            BoundExpression switchGoverningExpression,
            ImmutableArray<BoundPatternSwitchSection> switchSections,
            LabelSymbol defaultLabel,
            DiagnosticBag diagnostics)
        {
            var builder = new DecisionDagBuilder(compilation, defaultLabel, diagnostics);
            return builder.CreateDecisionDagForSwitchStatement(syntax, switchGoverningExpression, switchSections);
        }

        /// <summary>
        /// Create a decision dag for a switch expression.
        /// </summary>
        public static BoundDecisionDag CreateDecisionDagForSwitchExpression(
            CSharpCompilation compilation,
            SyntaxNode syntax,
            BoundExpression switchExpressionInput,
            ImmutableArray<BoundSwitchExpressionArm> switchArms,
            LabelSymbol defaultLabel,
            DiagnosticBag diagnostics)
        {
            var builder = new DecisionDagBuilder(compilation, defaultLabel, diagnostics);
            return builder.CreateDecisionDagForSwitchExpression(syntax, switchExpressionInput, switchArms);
        }

        /// <summary>
        /// Translate the pattern of an is-pattern expression.
        /// </summary>
        public static BoundDecisionDag CreateDecisionDagForIsPattern(
            CSharpCompilation compilation,
            SyntaxNode syntax,
            BoundExpression inputExpression,
            BoundPattern pattern,
            LabelSymbol defaultLabel,
            DiagnosticBag diagnostics,
            out LabelSymbol successLabel)
        {
            var builder = new DecisionDagBuilder(compilation, defaultLabel, diagnostics);
            return builder.CreateDecisionDagForIsPattern(syntax, inputExpression, pattern, out successLabel);
        }

        private BoundDecisionDag CreateDecisionDagForIsPattern(
            SyntaxNode syntax,
            BoundExpression inputExpression,
            BoundPattern pattern,
            out LabelSymbol successLabel)
        {
            successLabel = new GeneratedLabelSymbol("success");
            var rootIdentifier = new BoundDagTemp(inputExpression.Syntax, inputExpression.Type, null, 0);
            return MakeDecisionDag(syntax, ImmutableArray.Create(MakeTestsForPattern(1, pattern.Syntax, rootIdentifier, pattern, null, successLabel)));
        }

        private BoundDecisionDag CreateDecisionDagForSwitchStatement(
            SyntaxNode syntax,
            BoundExpression switchGoverningExpression,
            ImmutableArray<BoundPatternSwitchSection> switchSections)
        {
            var rootIdentifier = new BoundDagTemp(switchGoverningExpression.Syntax, switchGoverningExpression.Type, null, 0);
            int i = 0;
            var builder = ArrayBuilder<RemainingTestsForCase>.GetInstance(switchSections.Length);
            foreach (BoundPatternSwitchSection section in switchSections)
            {
                foreach (BoundPatternSwitchLabel label in section.SwitchLabels)
                {
                    if (label.Syntax.Kind() != SyntaxKind.DefaultSwitchLabel)
                    {
                        builder.Add(MakeTestsForPattern(++i, label.Syntax, rootIdentifier, label.Pattern, label.Guard, label.Label));
                    }
                }
            }

            return MakeDecisionDag(syntax, builder.ToImmutableAndFree());
        }

        /// <summary>
        /// Used to create a decision dag for a switch expression.
        /// </summary>
        private BoundDecisionDag CreateDecisionDagForSwitchExpression(
            SyntaxNode syntax,
            BoundExpression switchExpressionInput,
            ImmutableArray<BoundSwitchExpressionArm> switchArms)
        {
            var rootIdentifier = new BoundDagTemp(switchExpressionInput.Syntax, switchExpressionInput.Type, null, 0);
            int i = 0;
            var builder = ArrayBuilder<RemainingTestsForCase>.GetInstance(switchArms.Length);
            foreach (BoundSwitchExpressionArm arm in switchArms)
            {
                builder.Add(MakeTestsForPattern(++i, arm.Syntax, rootIdentifier, arm.Pattern, arm.Guard, arm.Label));
            }

            return MakeDecisionDag(syntax, builder.ToImmutableAndFree());
        }

        /// <summary>
        /// Compute the set of remaining tests for a pattern.
        /// </summary>
        private RemainingTestsForCase MakeTestsForPattern(
            int index,
            SyntaxNode syntax,
            BoundDagTemp input,
            BoundPattern pattern,
            BoundExpression guard,
            LabelSymbol label)
        {
            MakeAndSimplifyTestsAndBindings(input, pattern, out ImmutableArray<BoundDagTest> tests, out ImmutableArray<(BoundExpression, BoundDagTemp)> bindings);
            return new RemainingTestsForCase(index, syntax, tests, bindings, guard, label);
        }

        private void MakeAndSimplifyTestsAndBindings(
            BoundDagTemp input,
            BoundPattern pattern,
            out ImmutableArray<BoundDagTest> tests,
            out ImmutableArray<(BoundExpression, BoundDagTemp)> bindings)
        {
            var testsBuilder = ArrayBuilder<BoundDagTest>.GetInstance();
            var bindingsBuilder = ArrayBuilder<(BoundExpression, BoundDagTemp)>.GetInstance();
            MakeDecisionsAndBindings(input, pattern, testsBuilder, bindingsBuilder);
            SimplifyTestsAndBindings(testsBuilder, bindingsBuilder);
            tests = testsBuilder.ToImmutableAndFree();
            bindings = bindingsBuilder.ToImmutableAndFree();
        }

        private static void SimplifyTestsAndBindings(
            ArrayBuilder<BoundDagTest> testsBuilder,
            ArrayBuilder<(BoundExpression, BoundDagTemp)> bindingsBuilder)
        {
            // Now simplify the tests and bindings. We don't need anything in tests that does not
            // contribute to the result. This will, for example, permit us to match `(2, 3) is (2, _)` without
            // fetching `Item2` from the input.
            var usedValues = PooledHashSet<BoundDagEvaluation>.GetInstance();
            foreach ((var _, BoundDagTemp temp) in bindingsBuilder)
            {
                if (temp.Source != (object)null)
                {
                    usedValues.Add(temp.Source);
                }
            }

            for (int i = testsBuilder.Count - 1; i >= 0; i--)
            {
                switch (testsBuilder[i])
                {
                    case BoundDagEvaluation e:
                        {
                            if (usedValues.Contains(e))
                            {
                                if (e.Input.Source != (object)null)
                                {
                                    usedValues.Add(e.Input.Source);
                                }
                            }
                            else
                            {
                                testsBuilder.RemoveAt(i);
                            }
                        }
                        break;
                    case BoundDagTest d:
                        {
                            usedValues.Add(d.Input.Source);
                        }
                        break;
                    default:
                        throw ExceptionUtilities.UnexpectedValue(testsBuilder[i]);
                }
            }

            // We also do not need to compute any result more than once. This will permit us to fetch
            // a property once even if it is used more than once, e.g. `o is { X: P1, X: P2 }`
            usedValues.Clear();
            for (int i = 0; i < testsBuilder.Count; i++)
            {
                switch (testsBuilder[i])
                {
                    case BoundDagEvaluation e:
                        if (usedValues.Contains(e))
                        {
                            testsBuilder.RemoveAt(i);
                            i--;
                        }
                        else
                        {
                            usedValues.Add(e);
                        }
                        break;
                }
            }

            usedValues.Free();
        }

        private void MakeDecisionsAndBindings(
            BoundDagTemp input,
            BoundPattern pattern,
            ArrayBuilder<BoundDagTest> tests,
            ArrayBuilder<(BoundExpression, BoundDagTemp)> bindings)
        {
            switch (pattern)
            {
                case BoundDeclarationPattern declaration:
                    MakeDecisionsAndBindings(input, declaration, tests, bindings);
                    break;
                case BoundConstantPattern constant:
                    MakeDecisionsAndBindings(input, constant, tests, bindings);
                    break;
                case BoundDiscardPattern wildcard:
                    // Nothing to do. It always matches.
                    break;
                case BoundRecursivePattern recursive:
                    MakeDecisionsAndBindings(input, recursive, tests, bindings);
                    break;
                default:
                    throw new NotImplementedException(pattern.Kind.ToString());
            }
        }

        private void MakeDecisionsAndBindings(
            BoundDagTemp input,
            BoundDeclarationPattern declaration,
            ArrayBuilder<BoundDagTest> tests,
            ArrayBuilder<(BoundExpression, BoundDagTemp)> bindings)
        {
            TypeSymbol type = declaration.DeclaredType.Type;
            SyntaxNode syntax = declaration.Syntax;

            // Add a null and type test if needed.
            if (!declaration.IsVar)
            {
                input = ConvertToType(input, declaration.Syntax, type, tests);
            }

            BoundExpression left = declaration.VariableAccess;
            if (left != null)
            {
                Debug.Assert(left.Type == input.Type);
                bindings.Add((left, input));
            }
            else
            {
                Debug.Assert(declaration.Variable == null);
            }
        }

        private static void CheckNotNull(
            BoundDagTemp input,
            SyntaxNode syntax,
            ArrayBuilder<BoundDagTest> tests)
        {
            if (input.Type.CanContainNull())
            {
                // Add a null test
                tests.Add(new BoundDagNonNullTest(syntax, input));
            }
        }

        /// <summary>
        /// Generate a not-null check and a type check.
        /// </summary>
        private BoundDagTemp ConvertToType(
            BoundDagTemp input,
            SyntaxNode syntax,
            TypeSymbol type,
            ArrayBuilder<BoundDagTest> tests)
        {
            CheckNotNull(input, syntax, tests);
            if (input.Type != type)
            {
                TypeSymbol inputType = input.Type.StrippedType(); // since a null check has already been done
                HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                Conversion conversion = _conversions.ClassifyBuiltInConversion(inputType, type, ref useSiteDiagnostics);
                _diagnostics.Add(syntax, useSiteDiagnostics);
                if (input.Type.IsDynamic() ? type.SpecialType == SpecialType.System_Object : conversion.IsImplicit)
                {
                    // type test not needed, only the type cast
                }
                else
                {
                    // both type test and cast needed
                    tests.Add(new BoundDagTypeTest(syntax, type, input));
                }

                var evaluation = new BoundDagTypeEvaluation(syntax, type, input);
                input = new BoundDagTemp(syntax, type, evaluation, 0);
                tests.Add(evaluation);
            }

            return input;
        }

        private void MakeDecisionsAndBindings(
            BoundDagTemp input,
            BoundConstantPattern constant,
            ArrayBuilder<BoundDagTest> tests,
            ArrayBuilder<(BoundExpression, BoundDagTemp)> bindings)
        {
            if (constant.ConstantValue == ConstantValue.Null)
            {
                tests.Add(new BoundDagNullTest(constant.Syntax, input));
            }
            else
            {
                var convertedInput = ConvertToType(input, constant.Syntax, constant.Value.Type, tests);
                tests.Add(new BoundDagValueTest(constant.Syntax, constant.ConstantValue, convertedInput));
            }
        }

        /// <summary>
        /// This can be used instead of Debug.Assert as it more reliably breaks to the debugger
        /// when an assertion fails (it is unaffected by exception filters on enclosing frames).
        /// </summary>
        [Conditional("DEBUG")]
        private static void Assert(bool condition, string message = null)
        {
            if (!condition)
            {
                Debugger.Launch();
                Debugger.Break();
            }
        }

        private void MakeDecisionsAndBindings(
            BoundDagTemp input,
            BoundRecursivePattern recursive,
            ArrayBuilder<BoundDagTest> tests,
            ArrayBuilder<(BoundExpression, BoundDagTemp)> bindings)
        {
            Debug.Assert(input.Type.IsErrorType() || input.Type == recursive.InputType);
            if (recursive.DeclaredType != null && recursive.DeclaredType.Type != input.Type)
            {
                input = ConvertToType(input, recursive.Syntax, recursive.DeclaredType.Type, tests);
            }

            if (!recursive.Deconstruction.IsDefault)
            {
                // we have a "deconstruction" form, which is either an invocation of a Deconstruct method, or a disassembly of a tuple
                if (recursive.DeconstructMethodOpt != null)
                {
                    MethodSymbol method = recursive.DeconstructMethodOpt;
                    var evaluation = new BoundDagDeconstructEvaluation(recursive.Syntax, method, input);
                    tests.Add(evaluation);
                    int extensionExtra = method.IsStatic ? 1 : 0;
                    int count = Math.Min(method.ParameterCount - extensionExtra, recursive.Deconstruction.Length);
                    for (int i = 0; i < count; i++)
                    {
                        BoundPattern pattern = recursive.Deconstruction[i];
                        SyntaxNode syntax = pattern.Syntax;
                        var output = new BoundDagTemp(syntax, method.Parameters[i + extensionExtra].Type, evaluation, i);
                        MakeDecisionsAndBindings(output, pattern, tests, bindings);
                    }
                }
                else if (input.Type.IsTupleType)
                {
                    ImmutableArray<FieldSymbol> elements = input.Type.TupleElements;
                    ImmutableArray<TypeSymbol> elementTypes = input.Type.TupleElementTypes;
                    int count = Math.Min(elementTypes.Length, recursive.Deconstruction.Length);
                    for (int i = 0; i < count; i++)
                    {
                        BoundPattern pattern = recursive.Deconstruction[i];
                        SyntaxNode syntax = pattern.Syntax;
                        FieldSymbol field = elements[i];
                        var evaluation = new BoundDagFieldEvaluation(syntax, field, input); // fetch the ItemN field
                        tests.Add(evaluation);
                        var output = new BoundDagTemp(syntax, field.Type, evaluation, 0);
                        MakeDecisionsAndBindings(output, pattern, tests, bindings);
                    }
                }
                // PROTOTYPE(patterns2): Handle the ITuple case.
                else
                {
                    // This occurs in error cases.
                    Debug.Assert(recursive.HasAnyErrors);
                    // To prevent this pattern from subsuming other patterns and triggering a cascaded diagnostic, we add a test that will fail.
                    tests.Add(new BoundDagTypeTest(recursive.Syntax, ErrorType(), input, hasErrors: true));
                }
            }

            if (recursive.PropertiesOpt != null)
            {
                // we have a "property" form
                for (int i = 0; i < recursive.PropertiesOpt.Length; i++)
                {
                    (Symbol symbol, BoundPattern pattern) prop = recursive.PropertiesOpt[i];
                    Symbol symbol = prop.symbol;
                    BoundDagEvaluation evaluation;
                    switch (symbol)
                    {
                        case PropertySymbol property:
                            evaluation = new BoundDagPropertyEvaluation(prop.pattern.Syntax, property, input);
                            break;
                        case FieldSymbol field:
                            evaluation = new BoundDagFieldEvaluation(prop.pattern.Syntax, field, input);
                            break;
                        default:
                            Debug.Assert(recursive.HasAnyErrors);
                            tests.Add(new BoundDagTypeTest(recursive.Syntax, ErrorType(), input, hasErrors: true));
                            continue;
                    }

                    tests.Add(evaluation);
                    var output = new BoundDagTemp(prop.pattern.Syntax, prop.symbol.GetTypeOrReturnType(), evaluation, 0);
                    MakeDecisionsAndBindings(output, prop.pattern, tests, bindings);
                }
            }

            if (recursive.VariableAccess != null)
            {
                // we have a "variable" declaration
                bindings.Add((recursive.VariableAccess, input));
            }
        }

        private TypeSymbol ErrorType(string name = "")
        {
            return new ExtendedErrorTypeSymbol(this._compilation, name, arity: 0, errorInfo: null, unreported: false);
        }

        private BoundDecisionDag MakeDecisionDag(SyntaxNode syntax, ImmutableArray<RemainingTestsForCase> cases)
        {
            var defaultDecision = new BoundLeafDecisionDagNode(syntax, _defaultLabel);
            return MakeDecisionDag(cases, defaultDecision);
        }

        /// <summary>
        /// Compute and translate the decision dag, given a description of its initial state and a default
        /// decision when no decision appears to match. This implementation is nonrecursive to avoid
        /// overflowing the compiler's evaluation stack when compiling a large switch statement.
        /// </summary>
        private BoundDecisionDag MakeDecisionDag(ImmutableArray<RemainingTestsForCase> casesForRootNode, BoundLeafDecisionDagNode defaultDecision)
        {
            // A work list of DagStates whose successors need to be computed
            var workList = ArrayBuilder<DagState>.GetInstance();

            // A mapping used to make each DagState unique (i.e. to de-dup identical states).
            var uniqueState = new Dictionary<DagState, DagState>(DagStateEquivalence.Instance);

            // We "intern" the states, so that we only have a single object representing one
            // semantic state. Because the decision automaton may contain states that have more than one
            // predecessor, we want to represent each such state as a reference-unique object
            // so that it is processed only once. This object identity uniqueness will be important later when we
            // start mutating the DagState nodes to compute successors and BoundDecisionDagNodes
            // for each one.
            DagState uniqifyState(ImmutableArray<RemainingTestsForCase> cases)
            {
                var state = new DagState(cases);
                if (uniqueState.TryGetValue(state, out DagState existingState))
                {
                    return existingState;
                }
                else
                {
                    // When we add a new unique state, we add it to a work list so that we
                    // will process it to compute its successors.
                    uniqueState.Add(state, state);
                    workList.Push(state);
                    return state;
                }
            }

            var initialState = uniqifyState(casesForRootNode);

            // Go through the worklist of DagState nodes for which we have not yet computed
            // successor states.
            while (workList.Count != 0)
            {
                DagState state = workList.Pop();
                Debug.Assert(state.SelectedTest == null);
                Debug.Assert(state.TrueBranch == null);
                Debug.Assert(state.FalseBranch == null);
                if (state.Cases.IsDefaultOrEmpty)
                {
                    // If this state has no more cases that could possibly match, then
                    // we know there is no case that will match and this node represents a "default"
                    // decision. We do not need to compute a successor, as it is a leaf node
                    continue;
                }

                RemainingTestsForCase first = state.Cases[0];
                if (first.RemainingTests.IsDefaultOrEmpty)
                {
                    // The first of the remaining cases has fully matched, as there are no more tests to do.
                    // The language semantics of the switch statement and switch expression require that we
                    // execute the first matching case.
                    if (first.WhenClause == null || first.WhenClause.ConstantValue == ConstantValue.True)
                    {
                        // The when clause is satisfied also, so this is a leaf node
                    }
                    else
                    {
                        // In case the when clause fails, we prepare for the remaining cases.
                        var stateWhenFails = state.Cases.RemoveAt(0);
                        state.FalseBranch = uniqifyState(stateWhenFails);
                    }
                }
                else
                {
                    // Select the next test to do at this state, and compute successor states
                    switch (state.SelectedTest = state.ComputeSelectedTest())
                    {
                        case BoundDagEvaluation e:
                            state.TrueBranch = uniqifyState(RemoveEvaluation(state.Cases, e));
                            // An evaluation is considered to always succeed, so there is no false branch
                            break;
                        case BoundDagTest d:
                            SplitCases(state.Cases, d, out ImmutableArray<RemainingTestsForCase> whenTrueDecisions, out ImmutableArray<RemainingTestsForCase> whenFalseDecisions);
                            state.TrueBranch = uniqifyState(whenTrueDecisions);
                            state.FalseBranch = uniqifyState(whenFalseDecisions);
                            break;
                        case var n:
                            throw ExceptionUtilities.UnexpectedValue(n.Kind);
                    }
                }
            }

            var decisionDag = new DecisionDag(initialState);
            // Note: It is useful for debugging the dag state table construction to view `decisionDag.Dump()` here.
            workList.Free();

            // Now process the states in topological order, leaves first, and assign a BoundDecisionDag to each DagState.
            ImmutableArray<DagState> sortedStates = decisionDag.TopologicallySortedReachableStates();
            Debug.Assert(_defaultLabel != null);
            var finalStates = PooledDictionary<LabelSymbol, BoundDecisionDagNode>.GetInstance();
            finalStates.Add(_defaultLabel, defaultDecision);
            BoundDecisionDagNode finalState(SyntaxNode syntax, LabelSymbol label, ImmutableArray<(BoundExpression, BoundDagTemp)> bindings)
            {
                if (!finalStates.TryGetValue(label, out BoundDecisionDagNode final))
                {
                    final = new BoundLeafDecisionDagNode(syntax, label);
                    if (!bindings.IsDefaultOrEmpty)
                    {
                        final = new BoundWhenDecisionDagNode(syntax, bindings, null, final, null);
                    }

                    finalStates.Add(label, final);
                }

                return final;
            }

            for (int i = sortedStates.Length - 1; i >= 0; i--)
            {
                var state = sortedStates[i];
                if (state.Cases.IsDefaultOrEmpty)
                {
                    state.Dag = defaultDecision;
                    continue;
                }

                RemainingTestsForCase first = state.Cases[0];
                if (first.RemainingTests.IsDefaultOrEmpty)
                {
                    // The first case/pattern has fully matched
                    if (first.WhenClause == null || first.WhenClause.ConstantValue == ConstantValue.True)
                    {
                        state.Dag = finalState(first.Syntax, first.CaseLabel, first.Bindings);
                    }
                    else
                    {
                        // in case the when clause fails, we prepare for the remaining cases.
                        Debug.Assert(state.TrueBranch == null);
                        // The final state here does not need bindings, as they will be performed before evaluating the when
                        BoundDecisionDagNode whenTrue = finalState(first.Syntax, first.CaseLabel, default);
                        BoundDecisionDagNode whenFails = state.FalseBranch.Dag;
                        Debug.Assert(whenFails != null);
                        state.Dag = new BoundWhenDecisionDagNode(first.Syntax, first.Bindings, first.WhenClause, whenTrue, whenFails);
                    }
                }
                else
                {
                    switch (state.SelectedTest)
                    {
                        case BoundDagEvaluation e:
                            {
                                BoundDecisionDagNode next = state.TrueBranch.Dag;
                                Debug.Assert(next != null);
                                Debug.Assert(state.FalseBranch == null);
                                state.Dag = new BoundEvaluationDecisionDagNode(e.Syntax, e, next);
                            }
                            break;
                        case BoundDagTest d:
                            {
                                BoundDecisionDagNode whenTrue = state.TrueBranch.Dag;
                                BoundDecisionDagNode whenFalse = state.FalseBranch.Dag;
                                Debug.Assert(whenTrue != null);
                                Debug.Assert(whenFalse != null);
                                state.Dag = new BoundTestDecisionDagNode(d.Syntax, d, whenTrue, whenFalse);
                            }
                            break;
                        case var n:
                            throw ExceptionUtilities.UnexpectedValue(n.Kind);
                    }
                }
            }

            finalStates.Free();

            var rootDecisionDagNode = decisionDag.RootNode.Dag;
            Debug.Assert(rootDecisionDagNode != null);
            return new BoundDecisionDag(rootDecisionDagNode.Syntax, rootDecisionDagNode);
        }

        private void SplitCases(
            ImmutableArray<RemainingTestsForCase> cases,
            BoundDagTest d,
            out ImmutableArray<RemainingTestsForCase> whenTrue,
            out ImmutableArray<RemainingTestsForCase> whenFalse)
        {
            var whenTrueBuilder = ArrayBuilder<RemainingTestsForCase>.GetInstance();
            var whenFalseBuilder = ArrayBuilder<RemainingTestsForCase>.GetInstance();
            foreach (RemainingTestsForCase c in cases)
            {
                FilterCase(c, d, whenTrueBuilder, whenFalseBuilder);
            }

            whenTrue = whenTrueBuilder.ToImmutableAndFree();
            whenFalse = whenFalseBuilder.ToImmutableAndFree();
        }

        private void FilterCase(
            RemainingTestsForCase c,
            BoundDagTest d,
            ArrayBuilder<RemainingTestsForCase> whenTrueBuilder,
            ArrayBuilder<RemainingTestsForCase> whenFalseBuilder)
        {
            var trueBuilder = ArrayBuilder<BoundDagTest>.GetInstance();
            var falseBuilder = ArrayBuilder<BoundDagTest>.GetInstance();
            foreach (BoundDagTest other in c.RemainingTests)
            {
                CheckConsistentDecision(
                    test: d,
                    other: other,
                    syntax: d.Syntax,
                    trueTestPermitsTrueOther: out bool trueDecisionPermitsTrueOther,
                    falseTestPermitsTrueOther: out bool falseDecisionPermitsTrueOther,
                    trueTestImpliesTrueOther: out bool trueDecisionImpliesTrueOther,
                    falseTestImpliesTrueOther: out bool falseDecisionImpliesTrueOther);
                if (trueDecisionPermitsTrueOther)
                {
                    if (!trueDecisionImpliesTrueOther)
                    {
                        Debug.Assert(d != other);
                        trueBuilder?.Add(other);
                    }
                }
                else
                {
                    trueBuilder?.Free();
                    trueBuilder = null;
                }
                if (falseDecisionPermitsTrueOther)
                {
                    if (!falseDecisionImpliesTrueOther)
                    {
                        Debug.Assert(d != other);
                        falseBuilder?.Add(other);
                    }
                }
                else
                {
                    falseBuilder?.Free();
                    falseBuilder = null;
                }
            }

            RemainingTestsForCase makeNext(ArrayBuilder<BoundDagTest> remainingDecisions)
            {
                if (remainingDecisions.Count == c.RemainingTests.Length)
                {
                    remainingDecisions.Free();
                    return c;
                }
                else
                {
                    return new RemainingTestsForCase(c.Index, c.Syntax, remainingDecisions.ToImmutableAndFree(), c.Bindings, c.WhenClause, c.CaseLabel);
                }
            }

            if (trueBuilder != null)
            {
                var pcd = makeNext(trueBuilder);
                whenTrueBuilder.Add(pcd);
            }

            if (falseBuilder != null)
            {
                var pcd = makeNext(falseBuilder);
                whenFalseBuilder.Add(pcd);
            }
        }

        /// <summary>
        /// Given that the test <paramref name="test"/> has occurred and produced a true/false result,
        /// set some flags indicating the implied status of the <paramref name="other"/> test.
        /// </summary>
        /// <param name="test"></param>
        /// <param name="other"></param>
        /// <param name="trueTestPermitsTrueOther">set if <paramref name="test"/> being true would permit <paramref name="other"/> to succeed</param>
        /// <param name="falseTestPermitsTrueOther">set if a false result on <paramref name="test"/> would permit <paramref name="other"/> to succeed</param>
        /// <param name="trueTestImpliesTrueOther">set if <paramref name="test"/> being true means <paramref name="other"/> has been proven true</param>
        /// <param name="falseTestImpliesTrueOther">set if <paramref name="test"/> being false means <paramref name="other"/> has been proven true</param>
        private void CheckConsistentDecision(
            BoundDagTest test,
            BoundDagTest other,
            SyntaxNode syntax,
            out bool trueTestPermitsTrueOther,
            out bool falseTestPermitsTrueOther,
            out bool trueTestImpliesTrueOther,
            out bool falseTestImpliesTrueOther)
        {
            // innocent until proven guilty
            trueTestPermitsTrueOther = true;
            falseTestPermitsTrueOther = true;
            trueTestImpliesTrueOther = false;
            falseTestImpliesTrueOther = false;

            // if the tests are for unrelated things, there is no implication from one to the other
            if (test.Input != other.Input)
            {
                return;
            }

            // a test is consistent with itself
            if (test == other)
            {
                trueTestImpliesTrueOther = true;
                falseTestPermitsTrueOther = false;
                return;
            }

            switch (test)
            {
                case BoundDagNonNullTest n1:
                    switch (other)
                    {
                        case BoundDagValueTest v2:
                            // !(v != null) --> !(v == K)
                            falseTestPermitsTrueOther = false;
                            break;
                        case BoundDagNullTest v2:
                            // v != null --> !(v == null)
                            trueTestPermitsTrueOther = false;
                            // !(v != null) --> v == null
                            falseTestImpliesTrueOther = true;
                            break;
                        case BoundDagNonNullTest n2:
                            // v != null --> v != null
                            trueTestImpliesTrueOther = true;
                            // !(v != null) --> !(v != null)
                            falseTestPermitsTrueOther = false;
                            break;
                        default:
                            // !(v != null) --> !(v is T)
                            falseTestPermitsTrueOther = false;
                            break;
                    }
                    break;
                case BoundDagTypeTest t1:
                    switch (other)
                    {
                        case BoundDagNonNullTest n2:
                            // v is T --> v != null
                            trueTestImpliesTrueOther = true;
                            break;
                        case BoundDagTypeTest t2:
                            {
                                HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                                bool? matches = Binder.ExpressionOfTypeMatchesPatternType(_conversions, t1.Type, t2.Type, ref useSiteDiagnostics, out _);
                                if (matches == false)
                                {
                                    // If T1 could never be T2
                                    // v is T1 --> !(v is T2)
                                    trueTestPermitsTrueOther = false;
                                }
                                else if (matches == true)
                                {
                                    // If T1: T2
                                    // v is T1 --> v is T2
                                    trueTestImpliesTrueOther = true;
                                }

                                // If every T2 is a T1, then failure of T1 implies failure of T2.

                                matches = Binder.ExpressionOfTypeMatchesPatternType(_conversions, t2.Type, t1.Type, ref useSiteDiagnostics, out _);
                                _diagnostics.Add(syntax, useSiteDiagnostics);
                                if (matches == true)
                                {
                                    // If T2: T1
                                    // !(v is T1) --> !(v is T2)
                                    falseTestPermitsTrueOther = false;
                                }
                            }
                            break;
                        case BoundDagValueTest v2:
                            break;
                        case BoundDagNullTest v2:
                            // v is T --> !(v == null)
                            trueTestPermitsTrueOther = false;
                            break;
                    }
                    break;
                case BoundDagValueTest v1:
                    switch (other)
                    {
                        case BoundDagNonNullTest n2:
                            // v == K --> v != null
                            trueTestImpliesTrueOther = true;
                            break;
                        case BoundDagTypeTest t2:
                            break;
                        case BoundDagNullTest v2:
                            // v == K --> !(v == null)
                            trueTestPermitsTrueOther = false;
                            break;
                        case BoundDagValueTest v2:
                            if (v1.Value == v2.Value)
                            {
                                // if K1 == K2
                                // v == K1 --> v == K2
                                trueTestImpliesTrueOther = true;
                                // !(v == K1) --> !(v == K2)
                                falseTestPermitsTrueOther = false;
                            }
                            else
                            {
                                // if K1 != K2
                                // v == K1 --> !(v == K2)
                                trueTestPermitsTrueOther = false;
                                if (v1.Input.Type.SpecialType == SpecialType.System_Boolean)
                                {
                                    // As a special case, we note that boolean values can only ever be true or false.
                                    // !(v == true) --> v == false
                                    // !(v == false) --> v == true
                                    falseTestImpliesTrueOther = true;
                                }
                            }

                            break;
                    }
                    break;
                case BoundDagNullTest v1:
                    switch (other)
                    {
                        case BoundDagNonNullTest n2:
                            // v == null --> !(v != null)
                            trueTestPermitsTrueOther = false;
                            // !(v == null) --> v != null
                            falseTestImpliesTrueOther = true;
                            break;
                        case BoundDagTypeTest t2:
                            // v == null --> !(v is T)
                            trueTestPermitsTrueOther = false;
                            break;
                        case BoundDagNullTest v2:
                            // v == null --> v == null
                            trueTestImpliesTrueOther = true;
                            // !(v == null) --> !(v == null)
                            falseTestPermitsTrueOther = false;
                            break;
                        case BoundDagValueTest v2:
                            // v == null --> !(v == K)
                            trueTestPermitsTrueOther = false;
                            break;
                    }
                    break;
            }
        }

        private static ImmutableArray<RemainingTestsForCase> RemoveEvaluation(ImmutableArray<RemainingTestsForCase> cases, BoundDagEvaluation e)
        {
            return cases.SelectAsArray((c, eval) => RemoveEvaluation(c, eval), e);
        }

        private static RemainingTestsForCase RemoveEvaluation(RemainingTestsForCase c, BoundDagEvaluation e)
        {
            return new RemainingTestsForCase(
                Index: c.Index, Syntax: c.Syntax,
                RemainingTests: c.RemainingTests.WhereAsArray(d => !(d is BoundDagEvaluation e2) || e2 != e),
                Bindings: c.Bindings, WhenClause: c.WhenClause, CaseLabel: c.CaseLabel);
        }

        /// <summary>
        /// A representation of the entire decision dag and each of its states.
        /// </summary>
        private class DecisionDag
        {
            /// <summary>
            /// The starting point for deciding which case matches.
            /// </summary>
            public readonly DagState RootNode;
            public DecisionDag(DagState rootNode)
            {
                this.RootNode = rootNode;
            }

            /// <summary>
            /// A successor function used to topologically sort the DagState set.
            /// </summary>
            private static IEnumerable<DagState> Successor(DagState state)
            {
                if (state.TrueBranch != null)
                {
                    yield return state.TrueBranch;
                }

                if (state.FalseBranch != null)
                {
                    yield return state.FalseBranch;
                }
            }

            public ImmutableArray<DagState> TopologicallySortedReachableStates()
            {
                // Now process the states in topological order, leaves first, and assign a BoundDecisionDag to each DagState.
                return TopologicalSort.IterativeSort<DagState>(SpecializedCollections.SingletonEnumerable<DagState>(this.RootNode), Successor);
            }

#if DEBUG
            /// <summary>
            /// Starting with `this` state, produce a human-readable description of the state tables.
            /// This is very useful for debugging and optimizing the dag state construction.
            /// </summary>
            internal string Dump()
            {
                var allStates = this.TopologicallySortedReachableStates();
                var stateIdentifierMap = PooledDictionary<DagState, int>.GetInstance();
                for (int i = 0; i < allStates.Length; i++)
                {
                    stateIdentifierMap.Add(allStates[i], i);
                }

                int nextTempNumber = 0;
                var tempIdentifierMap = PooledDictionary<BoundDagEvaluation, int>.GetInstance();
                int tempIdentifier(BoundDagEvaluation e)
                {
                    return (e == null) ? 0 : tempIdentifierMap.TryGetValue(e, out int value) ? value : tempIdentifierMap[e] = ++nextTempNumber;
                }

                string tempName(BoundDagTemp t)
                {
                    return $"t{tempIdentifier(t.Source)}{(t.Index != 0 ? $".{t.Index.ToString()}" : "")}";
                }

                var resultBuilder = PooledStringBuilder.GetInstance();
                var result = resultBuilder.Builder;

                foreach (var state in allStates)
                {
                    result.AppendLine($"State " + stateIdentifierMap[state]);
                    foreach (RemainingTestsForCase cd in state.Cases)
                    {
                        result.Append($"  [{cd.Syntax}]");
                        foreach (BoundDagTest test in cd.RemainingTests)
                        {
                            result.Append($" {dump(test)}");
                        }

                        result.AppendLine();
                    }

                    if (state.SelectedTest != null)
                    {
                        result.AppendLine($"  Test: {dump(state.SelectedTest)}");
                    }

                    if (state.TrueBranch != null)
                    {
                        result.AppendLine($"  TrueBranch: {stateIdentifierMap[state.TrueBranch]}");
                    }

                    if (state.FalseBranch != null)
                    {
                        result.AppendLine($"  FalseBranch: {stateIdentifierMap[state.FalseBranch]}");
                    }
                }

                stateIdentifierMap.Free();
                tempIdentifierMap.Free();
                return resultBuilder.ToStringAndFree();

                string dump(BoundDagTest d)
                {
                    switch (d)
                    {
                        case BoundDagTypeEvaluation a:
                            return $"t{tempIdentifier(a)}={a.Kind}({a.Type.ToString()})";
                        case BoundDagEvaluation e:
                            return $"t{tempIdentifier(e)}={e.Kind}";
                        case BoundDagTypeTest b:
                            return $"?{d.Kind}({b.Type.ToString()}, {tempName(d.Input)})";
                        case BoundDagValueTest v:
                            return $"?{d.Kind}({v.Value.ToString()}, {tempName(d.Input)})";
                        default:
                            return $"?{d.Kind}({tempName(d.Input)})";
                    }
                }
            }
#endif
        }

        /// <summary>
        /// The state at a given node of the decision finite state automaton. This is used during computation of the state
        /// machine (<see cref="BoundDecisionDag"/>), and contains a representation of the meaning of the state. Because we always make
        /// forward progress when a test is evaluated (the state description is monotonically smaller at each edge), the
        /// graph of states is acyclic, which is why we call it a dag (directed acyclic graph).
        /// </summary>
        private class DagState
        {
            /// <summary>
            /// The set of cases that may still match, and for each of them the set of tests that remain to be tested.
            /// </summary>
            public readonly ImmutableArray<RemainingTestsForCase> Cases;

            public DagState(ImmutableArray<RemainingTestsForCase> cases)
            {
                this.Cases = cases;
            }

            // If not a leaf node or a when clause, the test that will be taken at this node of the
            // decision automaton.
            public BoundDagTest SelectedTest;

            // We only compute the dag states for the branches after we de-dup this DagState itself.
            // If all that remains is the `when` clauses, SelectedDecision is left `null` (we can
            // build the leaf node easily during translation) and the FalseBranch field is populated
            // with the successor on failure of the when clause (if one exists).
            public DagState TrueBranch, FalseBranch;

            // After the entire graph of DagState objects is complete, we translate each into its Dag node.
            public BoundDecisionDagNode Dag;

            /// <summary>
            /// Decide on what test to use at this node of the decision dag. This is the principal
            /// heuristic we can change to adjust the quality of the generated decision automaton.
            /// See https://www.cs.tufts.edu/~nr/cs257/archive/norman-ramsey/match.pdf for some ideas.
            /// </summary>
            internal BoundDagTest ComputeSelectedTest()
            {
                // Our simple heuristic is to perform the first test of the first possible matched case
                var choice = Cases[0].RemainingTests[0];

                // But if that test is a null check, it might be redundant with a following type test.
                if (choice.Kind == BoundKind.DagNonNullTest &&
                    Cases[0].RemainingTests.Length > 1 &&
                    Cases[0].RemainingTests[1] is var choice2 &&
                    choice2.Kind == BoundKind.DagTypeTest &&
                    choice.Input == choice2.Input)
                {
                    return choice2;
                }

                return choice;
            }
        }

        /// <summary>
        /// An equivalence relation between dag states used to dedup the states during dag construction.
        /// After dag construction is complete we treat a DagState as using object equality as equivalent
        /// states have been merged.
        /// </summary>
        private class DagStateEquivalence : IEqualityComparer<DagState>
        {
            public static readonly DagStateEquivalence Instance = new DagStateEquivalence();

            private DagStateEquivalence() { }

            public bool Equals(DagState x, DagState y)
            {
                return x.Cases.SequenceEqual(y.Cases, (a, b) => a.Equals(b));
            }

            public int GetHashCode(DagState x)
            {
                return Hash.Combine(Hash.CombineValues(x.Cases), x.Cases.Length);
            }
        }

        /// <summary>
        /// As part of the description of a node of the decision automaton, we keep track of what tests
        /// remain to be done for each case.
        /// </summary>
        private sealed class RemainingTestsForCase
        {
            /// <summary>
            /// A number that is distinct for each case and monotonically increasing from earlier to later cases.
            /// Since we always keep the cases in order, this is only used to assist with debugging (e.g.
            /// see DecisionDag.Dump()).
            /// </summary>
            public readonly int Index;
            public readonly SyntaxNode Syntax;
            public readonly ImmutableArray<BoundDagTest> RemainingTests;
            public readonly ImmutableArray<(BoundExpression, BoundDagTemp)> Bindings;
            public readonly BoundExpression WhenClause;
            public readonly LabelSymbol CaseLabel;
            public RemainingTestsForCase(
                int Index,
                SyntaxNode Syntax,
                ImmutableArray<BoundDagTest> RemainingTests,
                ImmutableArray<(BoundExpression, BoundDagTemp)> Bindings,
                BoundExpression WhenClause,
                LabelSymbol CaseLabel)
            {
                this.Index = Index;
                this.Syntax = Syntax;
                this.RemainingTests = RemainingTests;
                this.Bindings = Bindings;
                this.WhenClause = WhenClause;
                this.CaseLabel = CaseLabel;
            }

            public override bool Equals(object obj)
            {
                throw ExceptionUtilities.Unreachable;
            }

            public bool Equals(RemainingTestsForCase other)
            {
                // We do not include Syntax, Bindings, WhereClause, or CaseLabel
                // because once the Index is the same, those must be the same too.
                return other != null && this.Index == other.Index && this.RemainingTests.SequenceEqual(other.RemainingTests, SameTest);
            }

            private static bool SameTest(BoundDagTest x, BoundDagTest y)
            {
                if (x.Input != y.Input || x.Kind != y.Kind)
                {
                    return false;
                }

                switch (x.Kind)
                {
                    case BoundKind.DagTypeTest:
                        return ((BoundDagTypeTest)x).Type == ((BoundDagTypeTest)y).Type;

                    case BoundKind.DagValueTest:
                        return ((BoundDagValueTest)x).Value == ((BoundDagValueTest)y).Value;

                    default:
                        return true;
                }
            }

            public override int GetHashCode()
            {
                int result = Hash.Combine(RemainingTests.Length, Index);
                foreach (var d in RemainingTests)
                {
                    result = Hash.Combine((int)d.Kind, result);
                }

                return result;
            }
        }
    }
}
