﻿/* 
 * Copyright (c) 2016, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://raw.githubusercontent.com/ewoutkramer/fhir-net-api/master/LICENSE
 */

using Hl7.ElementModel;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Support;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hl7.Fhir.Validation
{
    internal static class TypeRefValidationExtensions
    {
        internal static OperationOutcome ValidateType(this Validator validator, ElementDefinition definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            validator.Trace(outcome, "Validating against constraints specified by the element's defined type", Issue.PROCESSING_PROGRESS, instance);

            if(definition.Type.Any(tr => tr.Code == null))
                validator.Trace(outcome, "ElementDefinition contains a type with an empty type code", Issue.PROFILE_ELEMENTDEF_CONTAINS_NULL_TYPE, instance);

            // Check if this is a choice: there are multiple distinct Codes to choose from
            var typeRefs = definition.Type.Where(tr => tr.Code != null);
            var choices = typeRefs.Select(tr => tr.Code.Value).Distinct();

            if (choices.Count() > 1)
            {
                if (instance.TypeName != null)
                {
                    // This is a choice type, find out what type is present in the instance data
                    // (e.g. deceased[Boolean], or _resourceType in json). This is exposed by IElementNavigator.TypeName.
                    var instanceType = ModelInfo.FhirTypeNameToFhirType(instance.TypeName);
                    if (instanceType != null)
                    {
                        // In fact, the next statements are just an optimalization, without them, we would do an ANY validation
                        // against *all* choices, what we do here is pre-filtering for sensible choices, and report if there isn't
                        // any.
                        var applicableChoices = typeRefs.Where(tr => ModelInfo.IsInstanceTypeFor(tr.Code.Value, instanceType.Value));

                        // Instance typename must be one of the applicable types in the choice
                        if (applicableChoices.Any())
                        {
                            outcome.Include(validator.ValidateTypeReferences(applicableChoices, instance));
                        }
                        else
                        {
                            var choiceList = String.Join(",", choices.Select(t => "'" + t.GetLiteral() + "'"));
                            validator.Trace(outcome, $"Type specified in the instance ('{instance.TypeName}') is not one of the allowed choices ({choiceList})",
                                     Issue.CONTENT_ELEMENT_HAS_INCORRECT_TYPE, instance);
                        }
                    }
                    else
                        validator.Trace(outcome, $"Instance indicates the element is of type '{instance.TypeName}', which is not a known FHIR core type.",
                                Issue.CONTENT_ELEMENT_CHOICE_INVALID_INSTANCE_TYPE, instance);
                }
                else
                    validator.Trace(outcome, "ElementDefinition is a choice or contains a polymorphic type constraint, but the instance does not indicate its actual type",
                        Issue.CONTENT_ELEMENT_CANNOT_DETERMINE_TYPE, instance);
            }
            else if (choices.Count() == 1)
            {
                // Only one type present in list of typerefs, all of the typerefs are candidates
                outcome.Include(validator.ValidateTypeReferences(typeRefs, instance));
            }

            return outcome;
        }

     
        internal static OperationOutcome ValidateTypeReferences(this Validator validator, IEnumerable<ElementDefinition.TypeRefComponent> typeRefs, IElementNavigator instance)
        {
            //TODO: It's more efficient to do the non-reference types FIRST, since ANY match would be ok,
            //and validating non-references is cheaper
            //TODO: For each choice, we will currently try to resolve the reference. If it fails, you'll get multiple errors and probably
            //better separate the fetching of the instance from the validation, so we do not run the rest of the validation (multiple times!)
            //when a reference cannot be resolved.  (this happens in a choice type where there are multiple references with multiple profiles)

            IEnumerable<Func<OperationOutcome>> validations = typeRefs.Select(tr => createValidatorForTypeRef(validator, instance,tr));
            return validator.Combine(BatchValidationMode.Any, instance, validations);
        }

        private static Func<OperationOutcome> createValidatorForTypeRef(Validator validator, IElementNavigator instance, ElementDefinition.TypeRefComponent tr)
        {
            // In STU3, we need to do BOTH
            // First, call Validate() against the profile (which is then a profile on Reference) THEN validate the referenced resource
            if (tr.Code == FHIRDefinedType.Reference)
                return () => validator.ValidateResourceReference(instance, tr);
            else
                return () => validator.Validate(instance,tr.GetDeclaredProfiles(), statedCanonicals: null, statedProfiles: null);
        }

        internal static OperationOutcome ValidateResourceReference(this Validator validator, IElementNavigator instance, ElementDefinition.TypeRefComponent typeRef)
        {
            var outcome = new OperationOutcome();

            var references = instance.GetChildrenByName("reference");
            var reference = references.FirstOrDefault()?.Value as string;

            if (reference == null)       // No reference found -> this is always valid
                return outcome;

            if(references.Count() > 1)
                validator.Trace(outcome, $"Encountered multiple references, just using first ({reference})", Issue.CONTENT_REFERENCE_HAS_MULTIPLE_REFERENCES, instance);

            // Try to resolve the reference *within* the current instance (Bundle, resource with contained resources) first
            ElementDefinition.AggregationMode? encounteredKind;
            var referencedResource = validator.ResolveReference(instance, reference, out encounteredKind, outcome);

            // Validate the kind of aggregation.
            // If no aggregation is given, all kinds of aggregation are allowed, otherwise only allow
            // those aggregation types that are given in the Aggregation element
            bool hasAggregation = typeRef.Aggregation != null && typeRef.Aggregation.Count() != 0;
            if(hasAggregation && !typeRef.Aggregation.Any(a => a == encounteredKind))
                  validator.Trace(outcome,  $"Encountered a reference ({reference}) of kind '{encounteredKind}' which is not allowed", Issue.CONTENT_REFERENCE_OF_INVALID_KIND, instance);

            // If we failed to find a referenced resource within the current instance, try to resolve it using an external method
            if (referencedResource == null && encounteredKind == ElementDefinition.AggregationMode.Referenced)
            {
                try
                {
                    referencedResource = validator.ExternalReferenceResolutionNeeded(reference, outcome, instance);
                }
                catch (Exception e)
                {
                    validator.Trace(outcome, $"Resolution of external reference {reference} failed. Message: {e.Message}",
                           Issue.UNAVAILABLE_REFERENCED_RESOURCE, instance);
                }
            }

            // If the reference was resolved (either internally or externally, validate it
            if (referencedResource != null)
            {
                validator.Trace(outcome, $"Starting validation of referenced resource {reference} ({encounteredKind})", Issue.PROCESSING_START_NESTED_VALIDATION, instance);

                // References within the instance are dealt with within the same validator,
                // references to external entities will operate within a new instance of a validator (and hence a new tracking context).
                // In both cases, the outcome is included in the result.
                if (encounteredKind != ElementDefinition.AggregationMode.Referenced)
                {
                    outcome.Include(validator.Validate(referencedResource, typeRef.GetDeclaredProfiles(), statedProfiles: null, statedCanonicals: null));
                }
                else
                {
                    var newValidator = validator.NewInstance();
                    outcome.Include(newValidator.Validate(referencedResource, typeRef.GetDeclaredProfiles(), statedProfiles: null, statedCanonicals: null));
                }
            }
            else
                validator.Trace(outcome, $"Cannot resolve reference {reference}", Issue.UNAVAILABLE_REFERENCED_RESOURCE, instance);

            return outcome;
        }

        private static IElementNavigator ResolveReference(this Validator validator, IElementNavigator instance, string reference, out ElementDefinition.AggregationMode? referenceKind, OperationOutcome outcome)
        {
            var identity = new ResourceIdentity(reference);

            if (identity.Form == ResourceIdentityForm.Undetermined)
            {
                if (!Uri.IsWellFormedUriString(reference, UriKind.RelativeOrAbsolute))
                {
                    validator.Trace(outcome, $"Encountered an unparseable reference ({reference})", Issue.CONTENT_UNPARSEABLE_REFERENCE, instance);
                    referenceKind = null;
                    return null;
                }
            }

            var result = validator.ScopeTracker.Resolve(instance, reference);

            if (identity.Form == ResourceIdentityForm.Local)
            {
                referenceKind = ElementDefinition.AggregationMode.Contained;
                if(result == null)
                    validator.Trace(outcome, $"Contained reference ({reference}) is not resolvable", Issue.CONTENT_CONTAINED_REFERENCE_NOT_RESOLVABLE, instance);
            }
            else
            {
                if (result != null)
                    referenceKind = ElementDefinition.AggregationMode.Bundled;
                else
                    referenceKind = ElementDefinition.AggregationMode.Referenced;
            }

            return result;
        }
    }
}
