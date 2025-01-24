using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;


using BTIS.Collections;
using BTIS.Contracts.Rating;
using BTIS.Linq;
using BTIS.Rating.Calculation;

namespace BTIS.Rating
{
    public class DefaultOperatorExpressionFactory : AbstractDefaultFactory<Expression, IOperatorExpressionInfo>, IOperatorExpressionFactory
    {
        protected override void ThrowIfInvalid(IOperatorExpressionInfo productIdentifier)
        {
            if (productIdentifier == null) { throw new ArgumentNullException(nameof(productIdentifier)); }
            if (string.IsNullOrEmpty(productIdentifier.Operator)) { throw new ArgumentException("productIdentifier operator cannot be null or empty."); }
        }
        protected override string MethodName(IOperatorExpressionInfo productIdentifier)
        {
            return productIdentifier.Operator;
        }
        protected override Type[] ArgumentTypes(IOperatorExpressionInfo productIdentifier)
        {
            return productIdentifier.ArgumentTypes ?? Array.Empty<Type>();
        }

        protected override object[] Arguments(IOperatorExpressionInfo productIdentifier)
        {
            return productIdentifier.Arguments ?? Array.Empty<object>();
        }

        private static MethodInfo FindWorkAround(string @operator, params Expression[] expressions)
        {
            if (expressions == null) { throw new ArgumentNullException(nameof(expressions)); }
            if (expressions.Length == 0) { throw new ArgumentException("Operators cannot have 0-arity.", nameof(expressions)); }
            if (expressions.Any(a => a == null)) { throw new ArgumentException("Cannot have null element.", nameof(expressions)); }

            List<Type> totalGenerics = new List<Type>();
            List<Type> currentOrder = new List<Type>();
            foreach (Type[] genericArguments in expressions.Select(e => Nullable.GetUnderlyingType(e.Type) == null ? e.Type.GetGenericArguments() : Type.EmptyTypes))
            {
                if (genericArguments.Length > 0)
                {
                    totalGenerics.AddRange(genericArguments);

                    if (currentOrder != null)
                    {
                        if (!genericArguments.Zip(currentOrder, (ga, cga) => ga == cga).All(v => v))
                        {
                            currentOrder = null;
                        }
                        else
                        {
                            if (genericArguments.Length > currentOrder.Count)
                            {
                                currentOrder.AddRange(genericArguments.Skip(currentOrder.Count));
                            }
                        }
                    }
                }
            }
            currentOrder = currentOrder ?? new List<Type>();
            Type workAroundType = typeof(WorkAround);

            if (totalGenerics.Count == 0)
            {
                return workAroundType.GetMethod(@operator, BindingFlags.Static | BindingFlags.Public, expressions.Select(e => e.Type).ToArray());
            }
            IList<Type[]> genericArgumentsToTry = new List<Type[]>();
            if (totalGenerics.Count > currentOrder.Count)
            {
                genericArgumentsToTry.Add(totalGenerics.ToArray());
            }
            if (currentOrder.Count > 0)
            {
                genericArgumentsToTry.Add(currentOrder.ToArray());
            }
            //string openWorkAroundTypeName = workAroundType.FullName;
            foreach (Type[] genericArguments in genericArgumentsToTry)
            {
                Type specificWorkAroundType = workAroundType.ConstructGenericDerivativeType(genericArguments);
                if (specificWorkAroundType == null) { continue; }
                //Type openWorkAroundType = Type.GetType(string.Format("{0}`{1}", openWorkAroundTypeName, genericArguments.Length));
                //if (openWorkAroundType == null) { continue; }
                //Type constructedWorkAroundType = openWorkAroundType.MakeGenericType(genericArguments);
                //if (constructedWorkAroundType == null) { continue; }
                MethodInfo methodInfo = specificWorkAroundType.GetMethod(@operator, BindingFlags.Static | BindingFlags.Public, expressions.Select(e => e.Type).ToArray());
                if (methodInfo != null) { return methodInfo; }
            }
            return null;
        }

        private static Expression New(Type typeToConstruct)
        {
            if (typeToConstruct == null) { throw new ArgumentNullException(nameof(typeToConstruct)); }

            if (typeToConstruct.CanInstantiate())
            {
                return Expression.New(typeToConstruct);
            }
            //FIXME: put this into servicelocator, using Microsoft DependencyInjection
            //handling the one type I know about
            if (typeToConstruct.IsConstructedGenericType)
            {
                if (typeToConstruct.GetGenericTypeDefinition().GenericTypeRootName() == typeof(BTIS.Contracts.Rating.IRatingResult).GenericTypeRootName())
                {
                    Type resultType = typeof(BTIS.Rating.Calculation.RatingResult).ConstructGenericDerivativeType(typeToConstruct.GetGenericArguments());
                    if (resultType != null && resultType.CanInstantiate())
                    {
                        return Expression.New(resultType);
                    }
                    throw ArgumentTypeException.ForArgumentTypeHasToBeInstantiable();
                }
            }
            throw ArgumentTypeException.ForAgumentTypeCouldNotCreateActivator(nameof(typeToConstruct));
        }

        private static Expression Add(Expression leftHandSide, Expression rightHandSide)
        {
            if (leftHandSide == null) { throw new ArgumentNullException(nameof(leftHandSide)); }
            if (rightHandSide == null) { throw new ArgumentNullException(nameof(rightHandSide)); }

            Type[] argumentTypes = new Type[] { leftHandSide.Type, rightHandSide.Type };
            BindingFlags bindingAttributes = BindingFlags.Static | BindingFlags.Public;


            MethodInfo methodInfo = leftHandSide.Type.GetMethod(CSharp.OperatorNames.Add, bindingAttributes, argumentTypes);
            if (methodInfo != null) { return Expression.Add(leftHandSide, rightHandSide, methodInfo); }

            methodInfo = rightHandSide.Type.GetMethod(CSharp.OperatorNames.Add, bindingAttributes, argumentTypes);
            if (methodInfo != null) { return Expression.Add(leftHandSide, rightHandSide, methodInfo); }

            methodInfo = FindWorkAround(nameof(Add), leftHandSide, rightHandSide);
            if (methodInfo != null) { return Expression.Call(methodInfo, leftHandSide, rightHandSide); }

            throw new UnknownOperatorException(expressions: new[] { leftHandSide, rightHandSide });
        }

        private static Expression Subtract(Expression leftHandSide, Expression rightHandSide)
        {
            if (leftHandSide == null) { throw new ArgumentNullException(nameof(leftHandSide)); }
            if (rightHandSide == null) { throw new ArgumentNullException(nameof(rightHandSide)); }

            Type[] argumentTypes = new Type[] { leftHandSide.Type, rightHandSide.Type };
            BindingFlags bindingAttributes = BindingFlags.Static | BindingFlags.Public;


            MethodInfo methodInfo = leftHandSide.Type.GetMethod(CSharp.OperatorNames.Subtract, bindingAttributes, argumentTypes);
            if (methodInfo != null) { return Expression.Subtract(leftHandSide, rightHandSide, methodInfo); }

            methodInfo = rightHandSide.Type.GetMethod(CSharp.OperatorNames.Subtract, bindingAttributes, argumentTypes);
            if (methodInfo != null) { return Expression.Subtract(leftHandSide, rightHandSide, methodInfo); }

            methodInfo = FindWorkAround(nameof(Subtract), leftHandSide, rightHandSide);
            if (methodInfo != null) { return Expression.Call(methodInfo, leftHandSide, rightHandSide); }

            throw new UnknownOperatorException(expressions: new[] { leftHandSide, rightHandSide });
        }

        private static Expression Multiply(Expression leftHandSide, Expression rightHandSide)
        {
            if (leftHandSide == null) { throw new ArgumentNullException(nameof(leftHandSide)); }
            if (rightHandSide == null) { throw new ArgumentNullException(nameof(rightHandSide)); }

            Type[] argumentTypes = new Type[] { leftHandSide.Type, rightHandSide.Type };
            BindingFlags bindingAttributes = BindingFlags.Static | BindingFlags.Public;


            MethodInfo methodInfo = leftHandSide.Type.GetMethod(CSharp.OperatorNames.Multiply, bindingAttributes, argumentTypes);
            if (methodInfo != null) { return Expression.Multiply(leftHandSide, rightHandSide, methodInfo); }

            methodInfo = rightHandSide.Type.GetMethod(CSharp.OperatorNames.Multiply, bindingAttributes, argumentTypes);
            if (methodInfo != null) { return Expression.Multiply(leftHandSide, rightHandSide, methodInfo); }

            methodInfo = FindWorkAround(nameof(Multiply), leftHandSide, rightHandSide);
            if (methodInfo != null) { return Expression.Call(methodInfo, leftHandSide, rightHandSide); }

            throw new UnknownOperatorException(expressions: new[] { leftHandSide, rightHandSide });
        }

        private static Expression Multiply(Expression leftHandSide, Expression rightHandSide, Expression tier)
        {
            if (leftHandSide == null) { throw new ArgumentNullException(nameof(leftHandSide)); }
            if (rightHandSide == null) { throw new ArgumentNullException(nameof(rightHandSide)); }
            if (tier == null) { throw new ArgumentNullException(nameof(tier)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Multiply), leftHandSide, rightHandSide, tier);
            if (methodInfo != null) { return Expression.Call(methodInfo, leftHandSide, rightHandSide, tier); }

            throw new UnknownOperatorException(expressions: new[] { leftHandSide, rightHandSide, tier });
        }

        private static Expression Divide(Expression leftHandSide, Expression rightHandSide)
        {
            if (leftHandSide == null) { throw new ArgumentNullException(nameof(leftHandSide)); }
            if (rightHandSide == null) { throw new ArgumentNullException(nameof(rightHandSide)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Divide), leftHandSide, rightHandSide);
            if (methodInfo != null) { return Expression.Call(methodInfo, leftHandSide, rightHandSide); }

            throw new UnknownOperatorException(expressions: new[] { leftHandSide, rightHandSide });
        }

        private static Expression Tally(Expression instance)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Tally), instance);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance); }

            throw new UnknownOperatorException(expressions: new[] { instance });
        }

        private static Expression Tally(Expression instance, Expression charges)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (charges == null) { throw new ArgumentNullException(nameof(charges)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Tally), instance, charges);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, charges); }

            throw new UnknownOperatorException(expressions: new[] { instance, charges });
        }

        private static Expression Tally(Expression instance, Expression charges, Expression tier)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (charges == null) { throw new ArgumentNullException(nameof(charges)); }
            if (tier == null) { throw new ArgumentNullException(nameof(tier)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Tally), instance, charges, tier);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, charges, tier); }

            throw new UnknownOperatorException(expressions: new[] { instance, charges, tier });
        }

        private static Expression Accumulate(Expression instance)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Accumulate), instance);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance); }

            throw new UnknownOperatorException(expressions: new[] { instance });
        }

        private static Expression Round(Expression instance, ConstantExpression places)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (places == null) { throw new ArgumentNullException(nameof(places)); }
            MethodInfo methodInfo = FindWorkAround(nameof(Round), instance, places);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, places); }

            throw new UnknownOperatorException(expressions: new[] { instance, places });
        }

        private static Expression Apply(Expression instance, Expression appliedToExpression)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (appliedToExpression == null) { throw new ArgumentNullException(nameof(appliedToExpression)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Apply), instance, appliedToExpression);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, appliedToExpression); }

            throw new UnknownOperatorException(expressions: new[] { instance, appliedToExpression });
        }

        private static Expression Apply(Expression instance, Expression technicalPremiumExpression, Expression exposureAmountExpression)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (technicalPremiumExpression == null) { throw new ArgumentNullException(nameof(technicalPremiumExpression)); }
            if (exposureAmountExpression == null) { throw new ArgumentNullException(nameof(exposureAmountExpression)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Apply), instance, technicalPremiumExpression, exposureAmountExpression);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, technicalPremiumExpression, exposureAmountExpression); }

            throw new UnknownOperatorException(expressions: new[] { instance, technicalPremiumExpression, exposureAmountExpression });
        }

        private static Expression Apply(Expression instance, Expression technicalPremiumExpression, Expression exposureAmountExpression, Expression tierExpression)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (technicalPremiumExpression == null) { throw new ArgumentNullException(nameof(technicalPremiumExpression)); }
            if (exposureAmountExpression == null) { throw new ArgumentNullException(nameof(exposureAmountExpression)); }
            if (tierExpression == null) { throw new ArgumentNullException(nameof(tierExpression)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Apply), instance, technicalPremiumExpression, exposureAmountExpression, tierExpression);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, technicalPremiumExpression, exposureAmountExpression, tierExpression); }

            throw new UnknownOperatorException(expressions: new[] { instance, technicalPremiumExpression, exposureAmountExpression, tierExpression });
        }

        private static Expression Apply(Expression instance, Expression technicalPremiumExpression, Expression exposureAmountExpression, Expression tierExpression, Expression regulatoryChargeExpression)
        {
            if (instance == null) { throw new ArgumentNullException(nameof(instance)); }
            if (technicalPremiumExpression == null) { throw new ArgumentNullException(nameof(technicalPremiumExpression)); }
            if (exposureAmountExpression == null) { throw new ArgumentNullException(nameof(exposureAmountExpression)); }
            if (tierExpression == null) { throw new ArgumentNullException(nameof(tierExpression)); }
            if (regulatoryChargeExpression == null) { throw new ArgumentNullException(nameof(regulatoryChargeExpression)); }

            MethodInfo methodInfo = FindWorkAround(nameof(Apply), instance, technicalPremiumExpression, exposureAmountExpression, tierExpression, regulatoryChargeExpression);
            if (methodInfo != null) { return Expression.Call(methodInfo, instance, technicalPremiumExpression, exposureAmountExpression, tierExpression, regulatoryChargeExpression); }

            throw new UnknownOperatorException(expressions: new[] { instance, technicalPremiumExpression, exposureAmountExpression, tierExpression, regulatoryChargeExpression });
        }

        private static Expression Max(Expression leftHandSide, Expression rightHandSide)
        {
            if (leftHandSide == null)
                throw new ArgumentNullException(nameof(leftHandSide));
            MethodInfo method = rightHandSide != null ? DefaultOperatorExpressionFactory.FindWorkAround(nameof(Max), leftHandSide, rightHandSide) : throw new ArgumentNullException(nameof(rightHandSide));
            if (method != (MethodInfo)null)
                return (Expression)Expression.Call(method, leftHandSide, rightHandSide);
            throw new UnknownOperatorException(nameof(Max), new Expression[2]
            {
        leftHandSide,
        rightHandSide
            });
        }

        static class KnownOperations
        {

        }

        static class WorkAround
        {
            //should this be decimal.MinValue instead of 0?
            public const decimal MinTier = 0M;
            public const decimal MinExposure = 0M;
            public const decimal MinTechnical = 0M;
            public const int DefaultPremiumTypeId = 0;

            private static Aggregator<IRateTier, IList<IRateTier>, IRateTier> CreateSimplifyAggregator()
            {
                return
                    new Aggregator<IRateTier, IList<IRateTier>, IRateTier>
                    (
                        new List<IRateTier>(),
                        (list, irt) => { list.Add(irt); return list; },
                        /* Any run of tiers with the same rate can be combined */
                        (list) =>
                        {
                            if (list.Count < 2) { return Enumerable.Empty<IRateTier>(); }
                            IRateTier first = list.First();
                            IRateTier last = list.Last();
                            if (first.Rate == last.Rate) { return Enumerable.Empty<IRateTier>(); }
                            list.Clear();
                            list.Add(last);
                            return Enumerable.Repeat(first, 1);
                        }
                        /* we may be left with a whole list of tiers with the same rate at the end */
                    );
            }

            private static Queue<IRateTier> InTierOrder(IEnumerable<IRateTier> rateTiers)
            {
                HashSet<(decimal, bool)> tiers = new HashSet<(decimal, bool)>();
                foreach (IRateTier rateTier in rateTiers ?? Enumerable.Empty<IRateTier>())
                {
                    if (!tiers.Add((rateTier.Tier, rateTier.Exclusive)))
                    {
                        throw new InvalidOperationException("RateTier had duplicate tier levels.");
                    }
                }
                if (!tiers.Any())
                {
                    return new Queue<IRateTier>(new[] { new RateTier(MinTier, null) });
                }
                return new Queue<IRateTier>(rateTiers.OrderBy(rt => rt.Tier).ThenBy(rt => rt.Exclusive ? 1 : 0));
            }

            private static IEnumerable<IRateTier> Combine(IEnumerable<IRateTier> leftHandSide, IEnumerable<IRateTier> rightHandSide, Func<decimal?, decimal?, decimal?> rateOperation)
            {
                Aggregator<IRateTier, IList<IRateTier>, IRateTier> simplifyAggregator = CreateSimplifyAggregator();

                Queue<IRateTier> leftRates = InTierOrder(leftHandSide);
                Queue<IRateTier> rightRates = InTierOrder(rightHandSide);

                decimal? currentLeftRate = null, currentRightRate = null;
                List<(decimal tier, bool exclusive)> candidates = new List<(decimal tier, bool exclusive)>(2);
                while (leftRates.Count > 0 || rightRates.Count > 0)
                {
                    IRateTier leftRate = null;
                    if (leftRates.Count > 0)
                    {
                        leftRate = leftRates.Peek();
                    }
                    IRateTier rightRate = null;
                    if (rightRates.Count > 0)
                    {
                        rightRate = rightRates.Peek();
                    }
                    candidates.Clear();
                    if (leftRate != null)
                    {
                        candidates.Add((leftRate.Tier, leftRate.Exclusive));
                    }
                    if (rightRate != null)
                    {
                        candidates.Add((rightRate.Tier, rightRate.Exclusive));
                    }

                    (decimal tier, bool exclusive) current = candidates.OrderBy(rt => rt.tier).ThenBy(rt => rt.exclusive ? 1 : 0).First();

                    if (leftRate?.Tier == current.tier && leftRate?.Exclusive == current.exclusive)
                    {
                        leftRates.Dequeue();
                        currentLeftRate = leftRate.Rate;
                    }
                    if (rightRate?.Tier == current.tier && rightRate?.Exclusive == current.exclusive)
                    {
                        rightRates.Dequeue();
                        currentRightRate = rightRate.Rate;
                    }
                    simplifyAggregator.MoveNext(new RateTier(current.tier, current.exclusive, rateOperation(currentLeftRate, currentRightRate)));
                    foreach (var result in simplifyAggregator.Yield())
                    {
                        yield return result;
                    }
                }
                // Handle what is leftover, these will all be the same rate
                // only one because Yield was called every loop
                foreach (var remainder in simplifyAggregator.Break())
                {
                    yield return remainder;
                }
            }

            public static IEnumerable<IRateTier> Simplify(IEnumerable<IRateTier> iRateTier)
            {
                //Assumed: InTierOrder returns at least one member always

                Aggregator<IRateTier, IList<IRateTier>, IRateTier> simplifyAggreggator = CreateSimplifyAggregator();
                simplifyAggreggator.Move(InTierOrder(iRateTier));
                foreach (IRateTier simplified in simplifyAggreggator.Yield())
                {
                    yield return simplified;
                }
                IList<IRateTier> remainder = simplifyAggreggator.Break();
                if (remainder.Count > 0)
                {
                    yield return remainder[0];
                }
            }

            public static IEnumerable<IRateTier> Add(IEnumerable<IRateTier> leftHandSide, IEnumerable<IRateTier> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, (l, r) => !l.HasValue ? (decimal?)null : !r.HasValue ? l.Value : (l.Value + r.Value));
            }

            public static IEnumerable<IRateTier> AddInvariant(IEnumerable<IRateTier> leftHandSide, IEnumerable<IRateTier> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, (l, r) => !l.HasValue && !r.HasValue ? (decimal?)null : ((l ?? 0M) + (r ?? 0M)));
            }
            public static IEnumerable<IRateTier> Subtract(IEnumerable<IRateTier> leftHandSide, IEnumerable<IRateTier> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, (l, r) => !l.HasValue ? (decimal?)null : !r.HasValue ? l.Value : (l.Value - r.Value));
            }
            public static IEnumerable<IRateTier> Multiply(IEnumerable<IRateTier> leftHandSide, IEnumerable<IRateTier> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, (l, r) => !l.HasValue ? (decimal?)null : !r.HasValue ? l.Value : (l.Value * r.Value));
            }

            public static decimal? Rate(IEnumerable<IRateTier> rateTiers, decimal tier)
            {
                decimal? result = null;
                Queue<IRateTier> tierOrder = InTierOrder(rateTiers);//make inclusive show up first
                while (tierOrder.Count > 0)
                {
                    IRateTier irt = tierOrder.Dequeue();
                    if (irt.Tier == tier) { return irt.Exclusive ? result : irt.Rate; }
                    if (tier < irt.Tier) { return result; }
                    result = irt.Rate;
                }
                return result;
            }

            public static decimal? Apportion(IPremiumPortion portion, decimal? charge = null, decimal? minimum = null)
            {
                if (portion == null) { return null; }

                decimal? actualCharge =
                    charge.HasValue
                    ?
                    minimum.HasValue ? Math.Max(charge.Value, minimum.Value) : charge.Value
                    :
                    minimum.HasValue ? minimum.Value : (decimal?)null
                    ;
                if (!actualCharge.HasValue) { return null; }

                return Math.Max(Math.Round(actualCharge.Value * portion.Factor, portion.Rounding, MidpointRounding.AwayFromZero), portion.Minimum);
            }

            public static ITechnicalPremium Accumulate(ILineItems items)
            {
                return new TechnicalPremium((items ?? Enumerable.Empty<ILineItem>()).Where(i => i != null && i.IsValid()).Sum(i => i.Amount() ?? 0M /* valid will have amounts*/));
            }

            private static Queue<IChargeTier> InTierOrder(IEnumerable<IChargeTier> chargeTiers)
            {
                HashSet<decimal> tiers = new HashSet<decimal>();

                foreach (IChargeTier chargeTier in chargeTiers ?? Enumerable.Empty<IChargeTier>())
                {
                    if (!tiers.Add(chargeTier.Tier))
                    {
                        throw new InvalidOperationException("ChargeTier had duplicate tier levels.");
                    }
                }
                if (!tiers.Any())
                {
                    return new Queue<IChargeTier>();
                }
                return new Queue<IChargeTier>(chargeTiers.OrderBy(rt => rt.Tier));
            }

            private static IEnumerable<IChargeTier> Charge(IEnumerable<IChargeTier> chargeTiers, decimal tier, bool isGraduate)
            {
                List<IChargeTier> result = new List<IChargeTier>();
                Queue<IChargeTier> tierOrder = InTierOrder(chargeTiers);
                while (tierOrder.Count > 0)
                {
                    IChargeTier ict = tierOrder.Dequeue();
                    if (ict.Tier == tier) { if (result.Count == 0) result.Add(ict); return result; }
                    if (tier < ict.Tier) { return result; }
                    if (!isGraduate) result.Clear();
                    result.Add(ict);
                }
                return result;
            }

            public static ILineItems Apply(ICharges charges, ITechnicalPremium technicalPremium)
            {
                return Apply(charges, technicalPremium?.Value ?? MinTechnical);
            }

            public static ILineItems Apply(ICharges charges, ITechnicalPremium technicalPremium, decimal? exposureAmount)
            {
                return Apply(charges, technicalPremium?.Value ?? MinTechnical, exposureAmount ?? MinExposure);
            }

            public static ILineItems Apply(ICharges charges, ITechnicalPremium technicalPremium, decimal? exposureAmount, ITechnicalPremium tierPremium)
            {
                return Apply(charges, technicalPremium?.Value ?? MinTechnical, exposureAmount ?? MinExposure, tierPremium?.Value ?? MinTier);
            }
            public static ILineItems Apply(ICharges charges, ITechnicalPremium technicalPremium, decimal? exposureAmount, ITechnicalPremium tierPremium, ICharge regulatoryCharge)
            {
                return Apply(charges, technicalPremium?.Value ?? MinTechnical, exposureAmount ?? MinExposure, tierPremium?.Value ?? MinTier, regulatoryCharge);
            }
            public static ILineItems Apply(ICharges charges, ITechnicalPremium technicalPremium, decimal? exposureAmount, decimal? tierPremium)
            {
                return Apply(charges, technicalPremium?.Value ?? MinTechnical, exposureAmount ?? MinExposure, tierPremium ?? MinTier);
            }

            private static ILineItems Apply(ICharges charges, decimal technicalAmount = MinTechnical, decimal exposureAmount = MinExposure, decimal tier = MinTier, ICharge regulatoryCharge = null)
            {
                if (charges.IsNullOrEmpty()) { return new LineItems(); }

                IEnumerable<ILineItem> items =
                    charges
                    .Where(c => !c.IsNullOrEmpty())
                    .SelectMany(c =>
                    {
                        ILineItem lineItem = Charge(c, technicalAmount, exposureAmount, tier);
                        return
                        lineItem == null
                        ?
                        Enumerable.Empty<ILineItem>()
                        :
                        Enumerable.Repeat(lineItem, 1)
                        ;
                    });

                return
                    new LineItems
                    (
                        regulatoryCharge == null ? null : regulatoryCharge.Amount(items.Amount()),
                        items
                    );
            }

            private static ILineItem Charge(ICharge charge, decimal technicalAmount, decimal exposureAmount, decimal tier)
            {
                IEnumerable<IChargeTier> chargeTiers = Charge(charge, tier, charge.IsGraduated);
                if (chargeTiers == null) { return null; }

                decimal premium = 0;

                foreach (var chargeTier in chargeTiers)
                {
                    if (charge.IsGraduated)
                    {
                        premium = chargeTier.Basis + Math.Round(chargeTier.TechnicalFactor * (chargeTier.Tier > technicalAmount ? chargeTier.Tier : technicalAmount - chargeTier.Tier), chargeTier.TechnicalRounding, MidpointRounding.AwayFromZero) + Math.Round(chargeTier.ExposureFactor * exposureAmount, chargeTier.ExposureRounding, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        premium = chargeTier.Basis + Math.Round(chargeTier.TechnicalFactor * technicalAmount, chargeTier.TechnicalRounding, MidpointRounding.AwayFromZero) + Math.Round(chargeTier.ExposureFactor * exposureAmount, chargeTier.ExposureRounding, MidpointRounding.AwayFromZero);
                    }
                }

                decimal earned = charge.Earned == null ? 0M : Math.Round(charge.Earned.Factor * premium, charge.Earned.Rounding, MidpointRounding.AwayFromZero);
                if (earned < charge.Earned?.Minimum) { earned = charge.Earned.Minimum; }

                decimal waived = charge.Waived == null ? 0M : Math.Round(charge.Waived.Factor * premium, charge.Waived.Rounding, MidpointRounding.AwayFromZero);
                if (waived < charge.Waived?.Minimum) { waived = charge.Waived.Minimum; }

                return
                    new LineItem
                    (
                        charge.Name,
                        charge.PremiumTypeId,
                        premium: premium,
                        clampMin: charge.Minimum,
                        earned: earned,
                        waived: waived
                    /*,financeable: financeable*/
                    );
            }

            public static ILineItems Apply(ICharge chargeFor, ILineItems itemCharges)
            {
                return Apply(chargeFor, itemCharges, MinTier);
            }

            public static ILineItems Apply(ICharge chargeFor, ILineItems itemCharges, ITechnicalPremium tier)
            {
                return Apply(chargeFor, itemCharges, tier?.Value ?? MinTier);
            }

            private static ILineItems Apply(ICharge chargeFor, ILineItems itemCharges, decimal tier)
            {
                if (chargeFor == null) { return new LineItems(); }
                if (itemCharges.IsNullOrEmpty()) { return new LineItems(Charge(chargeFor, 0M, 0M, tier)); }

                decimal
                    amount = 0M,
                    minimum = 0M,
                    earned = 0M,
                    waived = 0M;

                foreach (ILineItem itemCharge in itemCharges.Where(i => !i.IsNullOrEmpty()))
                {
                    if (!itemCharge.IsValid()) { return new LineItems(new LineItem(chargeFor.Name, chargeFor.PremiumTypeId)); }
                    amount += itemCharge.Amount() ?? 0M;/*valid will have amount*/
                    minimum += Math.Max(0M, itemCharge.ClampMinimum ?? 0M);
                    earned += Math.Max(0M, itemCharge.Earned ?? 0M);
                    waived += Math.Max(0M, itemCharge.Waived ?? 0M);
                }

                ILineItem tentativeLineItem = Charge(chargeFor, amount, 0M, tier);
                if (!tentativeLineItem.IsValid()) { return new LineItems(new LineItem(chargeFor.Name, chargeFor.PremiumTypeId)); }
                return
                    new LineItems
                    (
                        new LineItem
                        (
                            chargeFor.Name,
                            chargeFor.PremiumTypeId,
                            tentativeLineItem.Premium,
                            clampMin: Math.Max(minimum, tentativeLineItem.ClampMinimum ?? 0M),
                            waived: Math.Max(waived, tentativeLineItem.Waived ?? 0M),
                            earned: Math.Max(earned, tentativeLineItem.Earned ?? 0M)
                        )
                    );
            }


            public static ITechnicalPremium Add(ITechnicalPremium leftHandSide, ITechnicalPremium rightHandSide)
            {
                return
                    new TechnicalPremium
                    (
                        (leftHandSide?.Value ?? 0M)
                        + (rightHandSide?.Value ?? 0M)
                    );
            }
            public static ITechnicalPremium Subtract(ITechnicalPremium leftHandSide, ITechnicalPremium rightHandSide)
            {
                return
                    new TechnicalPremium
                    (
                        (leftHandSide?.Value ?? 0M)
                        - (rightHandSide?.Value ?? 0M)
                    );
            }

            public static ITechnicalPremium Divide(ITechnicalPremium leftHandSide, ITechnicalPremium rightHandSide)
            {
                return Divide(leftHandSide?.Value, rightHandSide?.Value);
            }

            public static ITechnicalPremium Divide(ITechnicalPremium leftHandSide, decimal? rightHandSide)
            {
                return Divide(leftHandSide?.Value, rightHandSide);
            }

            public static ITechnicalPremium Divide(decimal? leftHandSide, ITechnicalPremium rightHandSide)
            {
                return Divide(leftHandSide, rightHandSide?.Value);
            }

            public static ITechnicalPremium Divide(decimal? leftHandSide, decimal? rightHandSide)
            {
                if (!leftHandSide.HasValue) { return new TechnicalPremium(0M); }
                if (!rightHandSide.HasValue || rightHandSide.Value == 0M) { return new TechnicalPremium(1M); }
                return new TechnicalPremium(leftHandSide.Value / rightHandSide.Value);
            }
            public static ITechnicalPremium Round(ITechnicalPremium leftHandSide, int rightHandSide)
            {
                return Round(leftHandSide.Value, rightHandSide);
            }
            public static ITechnicalPremium Round(int leftHandSide, ITechnicalPremium rightHandSide)
            {
                return Round(rightHandSide.Value, leftHandSide);
            }
            public static ITechnicalPremium Round(decimal? leftHandSide, int rightHandSide)
            {
                if (!leftHandSide.HasValue) { return new TechnicalPremium(0M); }
                return new TechnicalPremium(Math.Round(leftHandSide??0, rightHandSide));
            }

            public static ITechnicalPremium Add(IFactor leftHandSide, ITechnicalPremium premium)
            {
                return new TechnicalPremium
                    (
                        (leftHandSide.IsNullOrEmpty() ? 0 : leftHandSide.First().Rate.Value)
                        + (premium?.Value ?? 0)
                    );
            }

            public static ITechnicalPremium Multiply(IFactor leftHandSide, ITechnicalPremium premium)
            {
                return new TechnicalPremium
                    (
                        (leftHandSide.IsNullOrEmpty() ? 0 : leftHandSide.First().Rate.Value)
                        * (premium?.Value ?? 0)
                    );
            }

            public static ITechnicalPremium Subtract(ITechnicalPremium premium, IFactor rightHandSide)
            {
                return new TechnicalPremium
                    (
                        (premium?.Value ?? 0) -
                        (rightHandSide.IsNullOrEmpty() ? 0 : rightHandSide.First().Rate.Value)
                    );
            }
            public static ITechnicalPremium Subtract(Decimal? leftHandSide, ITechnicalPremium premium)
            {
                return (ITechnicalPremium)new TechnicalPremium(Math.Max((leftHandSide ?? 0M) - (premium != null ? premium.Value : 0M), 0M));
            }

            public static ITechnicalPremium Max(Decimal? leftHandSide, ITechnicalPremium premium)
            {
                return (ITechnicalPremium)new TechnicalPremium(Math.Max(leftHandSide ?? 0M, premium != null ? premium.Value : 0M));
            }
        }

        static class WorkAround<TClassification>
        {
            public static readonly Factor<TClassification> Empty = new Factor<TClassification>("Empty", Enumerable.Empty<IFactorPart<TClassification>>());

            private static IDictionary<TKey, IEnumerable<IRateTier>> Reduce<TKey>(IEnumerable<IFactorPart<TClassification>> iFactorParts, Func<IFactorPart<TClassification>, bool> factorPartSelector, Func<IFactorPart<TClassification>, TKey> keySelector)
            {
                if (keySelector == null) { throw new ArgumentNullException(nameof(keySelector)); }
                if (iFactorParts == null || factorPartSelector == null) { return new Dictionary<TKey, IEnumerable<IRateTier>>(); }

                return
                    iFactorParts
                    .Where(factorPartSelector)
                    .ToLookup(keySelector, fp => (IEnumerable<IRateTier>)fp)
                    .ToDictionary(fp => fp.Key, fp => WorkAround.Simplify(fp.Aggregate(WorkAround.AddInvariant)))
                    ;
            }

            private static IDictionary<TClassification, IEnumerable<IRateTier>> Simplify(IFactor<TClassification> iFactor)
            {
                return Simplify((IEnumerable<IFactorPart<TClassification>>)iFactor);
            }

            private static IDictionary<TClassification, IEnumerable<IRateTier>> Simplify(IFactor<TClassification> iFactor, Func<IFactorPart<TClassification>, bool> factorPartSelector)
            {
                return Simplify((IEnumerable<IFactorPart<TClassification>>)iFactor, factorPartSelector == null ? (Func<IFactorPart<TClassification>, bool>)null : (fp) => !fp.IsNullOrEmpty() && factorPartSelector(fp));
            }

            private static IDictionary<TClassification, IEnumerable<IRateTier>> Simplify(IEnumerable<IFactorPart<TClassification>> iFactorParts)
            {
                return Simplify(iFactorParts, (fp) => !fp.IsNullOrEmpty());
            }

            private static IDictionary<TClassification, IEnumerable<IRateTier>> Simplify(IEnumerable<IFactorPart<TClassification>> iFactorParts, Func<IFactorPart<TClassification>, bool> factorPartSelector)
            {
                return Reduce(iFactorParts, factorPartSelector, fp => fp.Classification);
            }

            private static IFactor<TClassification> Combine(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide, Func<IEnumerable<IRateTier>, IEnumerable<IRateTier>, IEnumerable<IRateTier>> rateTierOperation, string name = "")
            {
                return Combine(leftHandSide, (IEnumerable<IFactor<TClassification>>)rightHandSide, rateTierOperation, name);
            }
            private static IFactor<TClassification> Combine(IFactor<TClassification> leftHandSide, IEnumerable<IFactor<TClassification>> rightHandSide, Func<IEnumerable<IRateTier>, IEnumerable<IRateTier>, IEnumerable<IRateTier>> rateTierOperation, string name = "")
            {
                if (leftHandSide.IsNullOrEmpty() || rateTierOperation == null) { return Empty; }
                if (rightHandSide.IsNullOrEmpty()) { return leftHandSide; }

                IDictionary<TClassification, IEnumerable<IRateTier>> result = Simplify(leftHandSide);
                HashSet<TClassification> resultKeys = new HashSet<TClassification>(result.Keys);

                foreach (var rightHandPart in
                    rightHandSide
                    .Select(rhs => Simplify(rhs, (rf) => resultKeys.Contains(rf.Classification)))
                    )
                {
                    foreach (var rkv in rightHandPart)
                    {
                        result[rkv.Key] = rateTierOperation(result[rkv.Key], rkv.Value);
                    }
                }

                return new Factor<TClassification>(name, result.Select(cfp => new FactorPart<TClassification>(cfp.Key, cfp.Value)));
            }


            public static IFactor<TClassification> Add(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, WorkAround.Add, "+");
            }
            public static IFactor<TClassification> Subtract(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, WorkAround.Subtract, "-");
            }
            public static IFactor<TClassification> Multiply(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, WorkAround.Multiply, "*");
            }

            public static IFactor<TClassification> Add(IFactor<TClassification> leftHandSide, IFactor<TClassification> rightHandSide)
            {
                return Combine(leftHandSide, Enumerable.Repeat(rightHandSide, 1), WorkAround.Add, "+");
            }
            public static IFactor<TClassification> Subtract(IFactor<TClassification> leftHandSide, IFactor<TClassification> rightHandSide)
            {
                return Combine(leftHandSide, Enumerable.Repeat(rightHandSide, 1), WorkAround.Subtract, "-");
            }
            public static IFactor<TClassification> Multiply(IFactor<TClassification> leftHandSide, IFactor<TClassification> rightHandSide)
            {
                return Combine(leftHandSide, Enumerable.Repeat(rightHandSide, 1), WorkAround.Multiply, "*");
            }

            public static IFactor<TClassification> Multiply(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide, ITechnicalPremium premium)
            {
                return Multiply(leftHandSide, rightHandSide, premium?.Value);
            }

            public static IFactor<TClassification> Multiply(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide, decimal? tier)
            {
                return Multiply(leftHandSide, rightHandSide, (tier ?? WorkAround.MinTier));
            }

            private static IFactor<TClassification> Multiply(IFactor<TClassification> leftHandSide, IFactors<TClassification> rightHandSide, decimal tier)
            {
                return
                    Combine(
                        Filter(leftHandSide, tier),
                        (rightHandSide ?? Enumerable.Empty<IFactor<TClassification>>())
                        .Where(rhs => !rhs.IsNullOrEmpty())
                        .Select(rhs => Filter(rhs, tier)),
                        WorkAround.Multiply,
                        "*"
                    );
            }

            public static ILineItems Tally(IFactor<TClassification> iFactor)
            {
                return Tally(iFactor, WorkAround.MinTier);
            }

            public static ILineItems Tally(IFactor<TClassification> iFactor, decimal tier)
            {
                return Tally(iFactor, null, tier);
            }

            public static ILineItems Tally(IFactor<TClassification> iFactor, ICharges<TClassification> charges)
            {
                return Tally(iFactor, charges, WorkAround.MinTier);
            }

            #region "Code duplicated so should be moved to common separate class"

            private static Queue<IChargeTier> InTierOrder(IEnumerable<IChargeTier> chargeTiers)
            {
                HashSet<decimal> tiers = new HashSet<decimal>();

                foreach (IChargeTier chargeTier in chargeTiers ?? Enumerable.Empty<IChargeTier>())
                {
                    if (!tiers.Add(chargeTier.Tier))
                    {
                        throw new InvalidOperationException("ChargeTier had duplicate tier levels.");
                    }
                }
                if (!tiers.Any())
                {
                    return new Queue<IChargeTier>();
                }
                return new Queue<IChargeTier>(chargeTiers.OrderBy(rt => rt.Tier));
            }

            private static IChargeTier Charge(IEnumerable<IChargeTier> chargeTiers, decimal tier)
            {
                IChargeTier result = null;
                Queue<IChargeTier> tierOrder = InTierOrder(chargeTiers);
                while (tierOrder.Count > 0)
                {
                    IChargeTier ict = tierOrder.Dequeue();
                    if (ict.Tier == tier) { return ict; }
                    if (tier < ict.Tier) { return result; }
                    result = ict;
                }
                return result;
            }
            #endregion "Code duplicated so should be moved to common separate class"


            public static ILineItems Tally(IFactor<TClassification> iFactor, ICharges<TClassification> charges, decimal tier)
            {
                if (iFactor.IsNullOrEmpty()) { return new LineItems(); }

                IDictionary<TClassification, ICharge<TClassification>> chargeDetailLookup = (charges?.ToDictionary(c => c.Classification, c => c)).ToDictionary(copy: false);
                int defaultPremiumTypeId = WorkAround.DefaultPremiumTypeId;
                if (chargeDetailLookup.Any() && !chargeDetailLookup.Any(c => c.Value == null))
                {
                    int cdlPremiumTypeId = chargeDetailLookup.First().Value.PremiumTypeId;
                    if (chargeDetailLookup.All(c => c.Value.PremiumTypeId == cdlPremiumTypeId))
                    {
                        defaultPremiumTypeId = cdlPremiumTypeId;
                    }
                }

                return
                    new LineItems
                    (
                        Filter(iFactor, tier)
                        .Select(f =>
                        {
                            ICharge<TClassification> charge;
                            if (!chargeDetailLookup.TryGetValue(f.Classification, out charge)) { charge = null; }
                            IChargeTier chargeTier = Charge((IEnumerable<IChargeTier>)charge, tier);
                            return Apply(chargeTier, f, charge?.PremiumTypeId ?? defaultPremiumTypeId, charge?.Minimum, charge?.Earned, charge?.Waived);
                        })
                    );
            }

            private static ILineItem Apply(IChargeTier chargeTier, IFactorPart<TClassification> factorPart, int premiumTypeId, decimal? minimum = 0M, IPremiumPortion earnedPortion = null, IPremiumPortion waivedPortion = null)
            {
                string name = $"{(factorPart == null ? string.Empty : factorPart.Classification.ToString())}";

                decimal? basePremium = factorPart?.SingleOrDefault()?.Rate;
                decimal? premium =
                    chargeTier == null
                    ?
                    basePremium
                    :
                    chargeTier.Basis + Decimal.Round(chargeTier.TechnicalFactor * (basePremium ?? 0M), chargeTier.TechnicalRounding, MidpointRounding.AwayFromZero);
                decimal portionPremium = premium ?? 0M;

                decimal earned = earnedPortion == null ? 0M : Math.Round(earnedPortion.Factor * portionPremium, earnedPortion.Rounding, MidpointRounding.AwayFromZero);
                if (earned < earnedPortion?.Minimum) { earned = earnedPortion.Minimum; }

                decimal waived = waivedPortion == null ? 0M : Math.Round(waivedPortion.Factor * portionPremium, waivedPortion.Rounding, MidpointRounding.AwayFromZero);
                if (waived < waivedPortion?.Minimum) { waived = waivedPortion.Minimum; }

                return
                    new LineItem
                    (
                        name,
                        premiumTypeId,
                        premium: premium,
                        clampMin: minimum,
                        earned: earned,
                        waived: waived
                    /*,financeable: financeable*/
                    );
            }


            public static IFactor<TClassification> Filter(IFactor<TClassification> iFactor, decimal tier)
            {
                if (iFactor.IsNullOrEmpty()) { return Empty; }

                return
                    new Factor<TClassification>
                    (
                        "filter",
                        Simplify(iFactor)
                        .Select(fp => new FactorPart<TClassification>(fp.Key, new[] { new RateTier(WorkAround.MinTier, WorkAround.Rate(fp.Value, tier)) }))
                    );
            }

            public static IFactor<TClassification> Round(IFactor<TClassification> iFactor, int places)
            {
                //if (iFactor.IsNullOrEmpty()) { return Empty; }

                return
                    new Factor<TClassification>
                    (
                        "round",
                        Reduce(iFactor, fp => !fp.IsNullOrEmpty(), fp => fp.Classification)
                        .Select(kvp =>
                            new FactorPart<TClassification>
                            (
                                kvp.Key,
                                kvp.Value.Select(rt => new RateTier(rt.Tier, rt.Exclusive, !rt.Rate.HasValue ? (decimal?)null : Math.Round(rt.Rate.Value, places, MidpointRounding.AwayFromZero)))
                            )
                        )
                    );
            }

            public static IFactor<TClassification> Multiply(IFactor<TClassification> leftHandSide, IFactor rightHandSide)
            {
                return
                    new Factor<TClassification>
                    (
                        "*",
                        Reduce(leftHandSide, fp => !fp.IsNullOrEmpty(), fp => fp.Classification)
                        .Select(kvp =>
                            new FactorPart<TClassification>
                            (
                                kvp.Key,
                                kvp.Value.Select(rt => new RateTier(rt.Tier, rt.Exclusive, rt.Rate.Value * (rightHandSide.IsNullOrEmpty() ? 0 : rightHandSide.First().Rate)))
                            )
                        )
                    );
            }

            public static IFactor<TClassification> Divide(IFactor<TClassification> leftHandSide, int divisionBy)
            {
                return
                    new Factor<TClassification>
                    (
                        "/",
                        Reduce(leftHandSide, fp => !fp.IsNullOrEmpty(), fp => fp.Classification)
                        .Select(kvp =>
                            new FactorPart<TClassification>
                            (
                                kvp.Key,
                                kvp.Value.Select(rt => new RateTier(rt.Tier, rt.Exclusive, rt.Rate.Value / divisionBy))
                            )
                        )
                    );
            }
        }

        static class WorkAround<TClassification, THazard>
        {
            public static readonly Factor<TClassification, THazard> Empty = new Factor<TClassification, THazard>("Empty", Enumerable.Empty<IFactorPart<TClassification, THazard>>());

            private static IDictionary<TKey, IEnumerable<IRateTier>> Reduce<TKey>(IEnumerable<IFactorPart<TClassification, THazard>> iFactorParts, Func<IFactorPart<TClassification, THazard>, bool> factorPartSelector, Func<IFactorPart<TClassification, THazard>, TKey> keySelector)
            {
                if (keySelector == null) { throw new ArgumentNullException(nameof(keySelector)); }
                if (iFactorParts == null || factorPartSelector == null) { return new Dictionary<TKey, IEnumerable<IRateTier>>(); }

                return
                    iFactorParts
                    .Where(factorPartSelector)
                    .ToLookup(keySelector, fp => (IEnumerable<IRateTier>)fp)
                    .ToDictionary(fp => fp.Key, fp => WorkAround.Simplify(fp.Aggregate(WorkAround.AddInvariant)))
                    ;
            }


            private static IDictionary<(TClassification Classification, THazard Hazard), IEnumerable<IRateTier>> Simplify(IFactor<TClassification, THazard> iFactor)
            {
                return Simplify((IEnumerable<IFactorPart<TClassification, THazard>>)iFactor);
            }

            private static IDictionary<(TClassification Classification, THazard Hazard), IEnumerable<IRateTier>> Simplify(IFactor<TClassification, THazard> iFactor, Func<IFactorPart<TClassification, THazard>, bool> factorPartSelector)
            {
                return Simplify((IEnumerable<IFactorPart<TClassification, THazard>>)iFactor, factorPartSelector == null ? (Func<IFactorPart<TClassification, THazard>, bool>)null : (fp) => !fp.IsNullOrEmpty() && factorPartSelector(fp));
            }

            private static IDictionary<(TClassification Classification, THazard Hazard), IEnumerable<IRateTier>> Simplify(IEnumerable<IFactorPart<TClassification, THazard>> iFactorParts)
            {
                return Simplify(iFactorParts, (fp) => !fp.IsNullOrEmpty());
            }

            private static IDictionary<(TClassification Classification, THazard Hazard), IEnumerable<IRateTier>> Simplify(IEnumerable<IFactorPart<TClassification, THazard>> iFactorParts, Func<IFactorPart<TClassification, THazard>, bool> factorPartSelector)
            {
                return Reduce(iFactorParts, factorPartSelector, fp => (Classification: fp.Classification, Hazard: fp.Hazard));
            }


            private static IFactor<TClassification, THazard> Combine(IFactor<TClassification, THazard> leftHandSide, IFactors<TClassification, THazard> rightHandSide, Func<IEnumerable<IRateTier>, IEnumerable<IRateTier>, IEnumerable<IRateTier>> rateTierOperation, string name = "")
            {
                return Combine(leftHandSide, (IEnumerable<IFactor<TClassification, THazard>>)rightHandSide, rateTierOperation, name);
            }
            private static IFactor<TClassification, THazard> Combine(IFactor<TClassification, THazard> leftHandSide, IEnumerable<IFactor<TClassification, THazard>> rightHandSide, Func<IEnumerable<IRateTier>, IEnumerable<IRateTier>, IEnumerable<IRateTier>> rateTierOperation, string name = "")
            {
                if (leftHandSide.IsNullOrEmpty() || rateTierOperation == null) { return Empty; }
                if (rightHandSide.IsNullOrEmpty()) { return leftHandSide; }

                IDictionary<(TClassification Classification, THazard Hazard), IEnumerable<IRateTier>> result = Simplify(leftHandSide);
                HashSet<(TClassification Classification, THazard Hazard)> resultKeys = new HashSet<(TClassification Classification, THazard Hazard)>(result.Keys);

                foreach (var rightHandPart in
                    rightHandSide
                    .Select(rhs => Simplify(rhs, (rf) => resultKeys.Contains((Classification: rf.Classification, Hazard: rf.Hazard))))
                    )
                {
                    foreach (var rkv in rightHandPart)
                    {
                        result[rkv.Key] = rateTierOperation(result[rkv.Key], rkv.Value);
                    }
                }

                return new Factor<TClassification, THazard>(name, result.Select(chfp => new FactorPart<TClassification, THazard>(chfp.Key.Classification, chfp.Key.Hazard, chfp.Value)));
            }


            public static IFactor<TClassification, THazard> Add(IFactor<TClassification, THazard> leftHandSide, IFactors<TClassification, THazard> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, WorkAround.Add, "+");
            }
            public static IFactor<TClassification, THazard> Subtract(IFactor<TClassification, THazard> leftHandSide, IFactors<TClassification, THazard> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, WorkAround.Subtract, "-");
            }

            public static IFactor<TClassification, THazard> Multiply(IFactor<TClassification, THazard> leftHandSide, IFactors<TClassification, THazard> rightHandSide)
            {
                return Combine(leftHandSide, rightHandSide, WorkAround.Multiply, "*");
            }

            public static IFactor<TClassification, THazard> Add(IFactor<TClassification, THazard> leftHandSide, IFactor<TClassification, THazard> rightHandSide)
            {
                return Combine(leftHandSide, Enumerable.Repeat(rightHandSide, 1), WorkAround.Add, "+");
            }
            public static IFactor<TClassification, THazard> Subtract(IFactor<TClassification, THazard> leftHandSide, IFactor<TClassification, THazard> rightHandSide)
            {
                return Combine(leftHandSide, Enumerable.Repeat(rightHandSide, 1), WorkAround.Subtract, "-");
            }
            public static IFactor<TClassification, THazard> Multiply(IFactor<TClassification, THazard> leftHandSide, IFactor<TClassification, THazard> rightHandSide)
            {
                return Combine(leftHandSide, Enumerable.Repeat(rightHandSide, 1), WorkAround.Multiply, "*");
            }



            public static IFactor<TClassification> Accumulate(IFactor<TClassification, THazard> iFactor)
            {
                if (iFactor.IsNullOrEmpty()) { return WorkAround<TClassification>.Empty; }

                return new Factor<TClassification>("accumulate", Reduce(iFactor, fp => !fp.IsNullOrEmpty(), fp => fp.Classification).Select(kvp => new FactorPart<TClassification>(kvp.Key, kvp.Value)));
            }
        }
    }


    static class WorkAroundExtensions
    {
        public static bool IsNullOrEmpty<TClassification, THazard>(this IFactors<TClassification, THazard> factors)
        {
            return IsNullOrEmpty((IEnumerable<IFactor<TClassification, THazard>>)factors);
        }
        public static bool IsNullOrEmpty<TClassification, THazard>(this IEnumerable<IFactor<TClassification, THazard>> factors)
        {
            return
                factors == null
                || !factors.Any()
                || !factors.Any(f => !f.IsNullOrEmpty())
                ;
        }
        public static bool IsNullOrEmpty<TClassification, THazard>(this IFactor<TClassification, THazard> factor)
        {
            return
                factor == null
                || !factor.Any()
                || !factor.Any(fp => !fp.IsNullOrEmpty())
                ;
        }
        public static bool IsNullOrEmpty<TClassification, THazard>(this IFactorPart<TClassification, THazard> factorPart)
        {
            return IsNullOrEmpty((IEnumerable<IRateTier>)factorPart);
        }

        public static bool IsNullOrEmpty<TClassification>(this IFactors<TClassification> factors)
        {
            return IsNullOrEmpty((IEnumerable<IFactor<TClassification>>)factors);
        }
        public static bool IsNullOrEmpty<TClassification>(this IEnumerable<IFactor<TClassification>> factors)
        {
            return
                factors == null
                || !factors.Any()
                || !factors.Any(f => !f.IsNullOrEmpty())
                ;
        }
        public static bool IsNullOrEmpty<TClassification>(this IFactor<TClassification> factor)
        {
            return
                factor == null
                || !factor.Any()
                || !factor.Any(fp => !fp.IsNullOrEmpty())
                ;
        }

        public static bool IsNullOrEmpty<TClassification>(this IFactorPart<TClassification> factorPart)
        {
            return IsNullOrEmpty((IEnumerable<IRateTier>)factorPart);
        }


        public static bool IsNullOrEmpty(this IFactors factors)
        {
            return IsNullOrEmpty((IEnumerable<IFactor>)factors);
        }

        public static bool IsNullOrEmpty(this IEnumerable<IFactor> factors)
        {
            return
                factors == null
                || !factors.Any()
                || !factors.Any(f => !f.IsNullOrEmpty())
                ;
        }

        public static bool IsNullOrEmpty(this IFactor factor)
        {
            return IsNullOrEmpty((IEnumerable<IRateTier>)factor);
        }

        public static bool IsNullOrEmpty(this IEnumerable<IRateTier> rateTiers)
        {
            return
                rateTiers == null
                || !rateTiers.Any()
                || !rateTiers.Any(rt => !rt.IsNullOrEmpty());
        }

        public static bool IsNullOrEmpty(this IRateTier rateTier)
        {
            return
                rateTier == null
                || !rateTier.Rate.HasValue
                /*|| rateTier.Tier == decimal.MaxValue*/
                ;
        }

        public static bool IsNullOrEmpty(this ICharges charges)
        {
            return
                charges == null
                || !charges.Any()
                || !charges.Any(c => !c.IsNullOrEmpty())
                ;
        }

        public static bool IsNullOrEmpty(this ICharge charge)
        {
            return
                charge == null
                // || (charge.Name == null && charge.HasNoValuesInside)
                ;
        }

        public static decimal? Amount(this ICharge charge, decimal? premium)
        {
            if (charge == null || !premium.HasValue) { return null; }
            var lineItem = CalculateAmount(premium, null, null, charge.Minimum, null, null);
            if (lineItem.IsValid) { return lineItem.Amount; }
            return null;
        }

        public static bool IsNullOrEmpty(this ILineItems items)
        {
            return
                items == null
                || !items.Any()
                || !items.Any(i => !i.IsNullOrEmpty())
                ;
        }

        public static bool IsNullOrEmpty(this ILineItem item)
        {
            return
                item == null
                // || !item.IsValid()
                ;
        }

        public static bool IsValid(this ILineItems items)
        {
            return
                items != null
                && items.Any()
                && !items.Any(i => i.IsNullOrEmpty() || !i.IsValid())
                ;
        }

        public static decimal? Amount(this ILineItems items)
        {
            return ((IEnumerable<ILineItem>)items).Amount();
        }

        public static decimal? Amount(this IEnumerable<ILineItem> items)
        {
            if (items == null) { return null; }
            decimal amount = 0M;
            foreach (var item in items)
            {
                var itemAmount = item.Amount();
                if (!itemAmount.HasValue) { return null; }
                amount += itemAmount.Value;
            }
            return amount;
        }

        public static bool IsValid(this ILineItem item)
        {
            return CalculateAmount(item).IsValid;
        }

        public static decimal? Amount(this ILineItem item)
        {
            return CalculateAmount(item).Amount;
        }

        public static LineItemAmount CalculateAmount(ILineItem lineItem)
        {
            return CalculateAmount(lineItem?.Premium, lineItem?.CapacityMinimum, lineItem?.CapacityMaximum, lineItem?.ClampMinimum, lineItem?.ClampMaximum, lineItem?.Waived);
        }
        private static LineItemAmount CalculateAmount(decimal? premium, decimal? capacityMinimum, decimal? capacityMaximum, decimal? clampMinimum, decimal? clampMaximum, decimal? waived)
        {
            bool
                valid = false,
                clampMin = false,
                clampMax = false,
                capacityMin = false,
                capacityMax = false;
            decimal?
                charge = null;

            if (premium.HasValue)
            {
                decimal amount = premium.Value;

                if (clampMinimum.HasValue && amount < clampMinimum.Value) { clampMin = true; amount = clampMinimum.Value; }
                if (clampMaximum.HasValue && clampMaximum.Value < amount) { clampMax = true; amount = clampMaximum.Value; }

                if (!(clampMin && clampMax))
                {
                    if (capacityMinimum.HasValue && amount < capacityMinimum.Value) { capacityMin = true; }
                    if (capacityMaximum.HasValue && capacityMaximum.Value < amount) { capacityMax = true; }

                    if (!(capacityMin || capacityMax))
                    {
                        if (waived.HasValue)
                        {
                            amount -= waived.Value;
                            if (amount < 0M) { amount = 0M; }
                        }

                        charge = amount;
                        valid = true;
                    }
                }
            }

            return new LineItemAmount(valid, capacityMin, capacityMax, clampMin, clampMax, charge);
        }

    }
}
