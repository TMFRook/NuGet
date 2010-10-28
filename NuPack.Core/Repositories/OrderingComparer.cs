﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace NuGet {
    internal class OrderingComparer<TElement> : ExpressionVisitor, IComparer<TElement> {
        private static readonly string[] _orderMethods = new[] { "OrderBy", "ThenBy", "OrderByDescending", "ThenByDescending" };
        
        private readonly Expression _expression;        
        private readonly Dictionary<ParameterExpression, ParameterExpression> _parameters = new Dictionary<ParameterExpression, ParameterExpression>();
        
        private bool _inOrderExpression;
        private Stack<Ordering<TElement>> _orderings;

        public OrderingComparer(Expression expression) {
            _expression = expression;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node) {
            if (_orderMethods.Any(m => QueryableHelper.IsQueryableMethod(node, m))) {
                _inOrderExpression = true;

                // The lambdas are wrapped in a unary expression
                var unaryExpression = (UnaryExpression)Visit(node.Arguments[1]);
                var lambda = (Expression<Func<TElement, IComparable>>)unaryExpression.Operand;

                // Push the sort expression on the stack so we can compare later
                _orderings.Push(new Ordering<TElement> {
                    Descending = node.Method.Name.EndsWith("Descending"),
                    Extractor = lambda.Compile()
                });

                _inOrderExpression = false;
            }
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitLambda<T>(Expression<T> node) {
            if (_inOrderExpression) {
                Expression body = Expression.Convert(Visit(node.Body), typeof(IComparable));
                var parameters = node.Parameters.Select(Visit).Cast<ParameterExpression>();
                return Expression.Lambda<Func<TElement, IComparable>>(body, parameters.ToArray());
            }
            return base.VisitLambda<T>(node);
        }

        protected override Expression VisitParameter(ParameterExpression node) {
            if (_inOrderExpression) {
                ParameterExpression value;
                if (!_parameters.TryGetValue(node, out value)) {
                    value = Expression.Parameter(node.Type);
                    _parameters[node] = value;
                }
                return value;
            }
            return base.VisitParameter(node);
        }

        public int Compare(TElement x, TElement y) {
            if (_orderings == null) {
                _orderings = new Stack<Ordering<TElement>>();
                Visit(_expression);
            }

            if (!_orderings.Any()) {
                // TODO: Throw
            }

            int value = 0;
            foreach (var ordering in _orderings) {
                IComparable left = ordering.Extractor(x);
                IComparable right = ordering.Extractor(y);

                // TODO: Handle null

                value = left.CompareTo(right);
                if (value != 0) {
                    if (ordering.Descending) {
                        return -value;
                    }
                    return value;
                }
            }

            return value;
        }

        private class Ordering<T> {
            public Func<T, IComparable> Extractor { get; set; }
            public bool Descending { get; set; }
        }
    }
}