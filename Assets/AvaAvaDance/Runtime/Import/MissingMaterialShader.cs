using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvaAvaDance.Import
{
    internal sealed class MissingMaterialShader
    {
        internal string Guid, ExactName, Label;
        internal long FileId;
        internal Material Material;
        internal Action<Shader,List<UnityEngine.Object>> Apply;
        internal Shader Resolve(ShaderLibrary library) => library.ResolveMaterial(Guid,FileId,ExactName);
    }
}
