using NUnit.Framework;
using SmilemanStudio.Core.Voxels;
using System;

namespace SmilemanStudio.Tests.EditMode
{
    /// <summary>
    /// Тесты enum-типов VoxelMaterial и VoxelBrushKind (ADR-0002, D7, D5).
    /// </summary>
    public class VoxelEnumTypesTests
    {
        [Test]
        public void VoxelMaterial_None_IsZero()
        {
            Assert.AreEqual(0, (int)VoxelMaterial.None,
                "None обязан быть 0 по правилу AGENTS.md.");
        }

        [Test]
        public void VoxelMaterial_DefaultValue_IsNone()
        {
            // default(enum) = 0 = None — невалидное значение, а не "Dirt".
            Assert.AreEqual(VoxelMaterial.None, default(VoxelMaterial),
                "default(VoxelMaterial) должен быть None.");
        }

        [Test]
        public void VoxelMaterial_HasAllRequiredValues()
        {
            // dirt, coal, sand, quartz — все должны присутствовать.
            Assert.AreEqual(1, (int)VoxelMaterial.Dirt);
            Assert.AreEqual(2, (int)VoxelMaterial.Coal);
            Assert.AreEqual(3, (int)VoxelMaterial.Sand);
            Assert.AreEqual(4, (int)VoxelMaterial.Quartz);
        }

        [Test]
        public void VoxelBrushKind_None_IsZero()
        {
            Assert.AreEqual(0, (int)VoxelBrushKind.None,
                "None обязан быть 0 по правилу AGENTS.md.");
        }

        [Test]
        public void VoxelBrushKind_DefaultValue_IsNone()
        {
            Assert.AreEqual(VoxelBrushKind.None, default(VoxelBrushKind),
                "default(VoxelBrushKind) должен быть None.");
        }

        [Test]
        public void VoxelBrushKind_HasAddAndSubtract()
        {
            Assert.AreEqual(1, (int)VoxelBrushKind.Add,
                "Add (min SDF) обязан присутствовать.");
            Assert.AreEqual(2, (int)VoxelBrushKind.Subtract,
                "Subtract (max SDF) обязан присутствовать.");
        }

        [Test]
        public void VoxelMaterial_IsBackedByByte()
        {
            Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(VoxelMaterial)),
                "VoxelMaterial обязан быть byte для компактности.");
        }

        [Test]
        public void VoxelBrushKind_IsBackedByByte()
        {
            Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(VoxelBrushKind)),
                "VoxelBrushKind обязан быть byte для компактности.");
        }
    }
}
