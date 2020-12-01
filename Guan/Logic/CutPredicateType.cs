﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Guan.Logic
{
    /// <summary>
    /// Predicate tyoe for cut(!).
    /// </summary>
    internal class CutPredicateType : PredicateType
    {
        class Resolver : BooleanPredicateResolver
        {
            public Resolver()
                : base(null, null, null)
            {
            }

            protected override bool Check()
            {
                return true;
            }
        }

        public static readonly CutPredicateType Singleton = new CutPredicateType();

        private CutPredicateType()
            : base("!")
        {
        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver();
        }
    }
}
