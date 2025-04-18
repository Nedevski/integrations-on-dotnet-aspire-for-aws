// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.AWS.CloudFormation;

/// <summary>
/// Annotations that records a reference of CloudFormation resources to target resources like projects.
/// </summary>
/// <param name="targetResource"></param>
[DebuggerDisplay("Type = {GetType().Name,nq}, TargetResource = {TargetResource}")]
internal sealed class CloudFormationReferenceAnnotation(string targetResource) : IResourceAnnotation
{
    /// <summary>
    /// The name of the target resource.
    /// </summary>
    internal string TargetResource { get; } = targetResource;
}
