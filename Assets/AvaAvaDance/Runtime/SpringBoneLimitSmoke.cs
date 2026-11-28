using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniGLTF.SpringBoneJobs;
using UnityEngine;
using UnityEngine.UIElements;

namespace AvaAvaDance
{
    public sealed partial class DanceApp
    {
        async Task CheckSpringBoneLimits(string path, string directory)
        {
            void Require(bool condition, string label)
            {
                if (!condition) throw new InvalidOperationException(label);
            }
            async Task WaitForSimulation()
            {
                for (int i = 0; i < 16; i++) await Task.Yield();
            }
            async Task<Actor> Import()
            {
                var model = ReadVrmFile(path);
                await ImportModelAsync(model, new() { model }, null);
                return actor;
            }
            Vector3 Direction(Actor owner) => owner.springJoints.Values.Single().Component.transform.localRotation * Vector3.up;
            void Angle(Actor owner, float expected, string test)
            {
                float measured = Vector3.Angle(Vector3.up, Direction(owner));
                Require(Mathf.Abs(measured - expected) < 1f, $"{test}: expected {expected}°, measured {measured}°");
            }
            void Click(string name) => McpSend(modal.Q<Button>(name), NavigationSubmitEvent.GetPooled());
            void Edit(AnglelimitTypes type, float pitch = 0, float yaw = 0, Vector3 axis = default)
            {
                Require(Execute(Command.SpringBoneLimits), "Open angle limit editor");
                Require(modal.Q<DropdownField>("spring-bone-joint").choices.Count == 1, "Tail has no angle limit control");
                modal.Q<DropdownField>("spring-bone-limit-mode").index = (int)type;
                modal.Q<Slider>("spring-bone-pitch").value = pitch;
                modal.Q<Slider>("spring-bone-yaw").value = yaw;
                modal.Q<Vector3Field>("spring-bone-rotation").value = axis;
                Click("spring-bone-apply");
                Require(actor.springJoints.Values.Single().Component.m_anglelimitType == type, "Apply edited mode");
                CloseDialog();
            }

            Time.captureFramerate = 60;
            Require(!Available(Command.SpringBoneLimits), "Scene has no spring joints");
            var first = await Import();
            await WaitForSimulation();
            Require(first.springJoints.Values.Single().Original.mode == AnglelimitTypes.Cone, "Read VRMC_springBone_limit 1.0");
            Angle(first, 25, "Authored cone");
            string original = Path.Combine(directory, "original-spring-bone.avaproject");
            await SaveProjectAsync(original, new UniGLTF.RuntimeOnlyAwaitCaller());
            Edit(AnglelimitTypes.None);
            await WaitForSimulation();
            Require(Vector3.Angle(Vector3.up, Direction(first)) > 60, "Turning off the authored limit allows a wider bend");
            Edit(AnglelimitTypes.Hinge, 40);
            await WaitForSimulation();
            Require(Mathf.Abs(Direction(first).x) < .01f, "Hinge confines the spring to its plane");
            Edit(AnglelimitTypes.Spherical, 0, 0, new Vector3(0, 0, 15));
            await WaitForSimulation();
            Angle(first, 15, "Spherical offset");
            Require(Direction(first).x < 0 && ProjectChanged(), "Offset orientation and unsaved edit");

            var second = await Import();
            await WaitForSimulation();
            Angle(first, 15, "First model after import rebuild");
            Angle(second, 25, "Second model retains authored limits");
            RemoveCurrentAvatar();
            await WaitForSimulation();
            Angle(first, 15, "First model after removal rebuild");
            second = await Import();
            Edit(AnglelimitTypes.Cone, 5);
            await WaitForSimulation();
            Angle(second, 5, "Second model edited independently");

            string project = Path.Combine(directory, "spring-bone.avaproject");
            await SaveProjectAsync(project, new UniGLTF.RuntimeOnlyAwaitCaller());
            await OpenProjectAsync(project);
            await WaitForSimulation();
            Require(!ProjectChanged() && drawOrder.Count == 2, "Reopen saved limits");
            Angle(drawOrder[0], 15, "First model limit restored");
            Angle(drawOrder[1], 5, "Second model limit restored");

            var invalid = CaptureProject();
            var duplicate = invalid.springBoneLimits.Single();
            invalid.springBoneLimits = new[] { duplicate, duplicate };
            string bad = Path.Combine(directory, "invalid-spring-bone.avaproject");
            WriteProjectFile(bad, invalid);
            bool rejected = false;
            try { await OpenProjectAsync(bad); }
            catch (InvalidDataException) { rejected = true; }
            await WaitForSimulation();
            Require(rejected && !ProjectChanged() && projectPath == project && drawOrder.Count == 2, "Invalid project rolled back");
            Angle(drawOrder[0], 15, "First model after rollback");
            Angle(drawOrder[1], 5, "Second model after rollback");

            ChooseMode(drawOrder[0].label);
            Require(Execute(Command.SpringBoneLimits), "Open editor for reset");
            Click("spring-bone-restore");
            CloseDialog();
            await WaitForSimulation();
            Angle(drawOrder[0], 25, "Restore authored limit");
            Angle(drawOrder[1], 5, "Reset affects one model");
            Require(drawOrder[0].springBoneLimits.Length == 0, "Reset removes the override");
            await OpenProjectAsync(original);
            await WaitForSimulation();
            Require(drawOrder.Count == 1 && !ProjectChanged(), "Open project with no overrides");
            Angle(drawOrder[0], 25, "No override preserves authored angle");
        }
    }
}
