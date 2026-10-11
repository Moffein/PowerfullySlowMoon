using BepInEx;
using BepInEx.Configuration;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Security;
using System.Security.Permissions;
using System;
using UnityEngine.AddressableAssets;
using EntityStates;
using RoR2.CharacterSpeech;
using RoR2.Networking;

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace PowerfullySlow
{
    [BepInPlugin("xyz.yekoc.PowerfullySlow", "Powerfully Slow Moon", "1.4.1")]
    [BepInIncompatibility("com.xoxfaby.UnlockAll")]
    [BepInDependency("com.KingEnderBrine.InLobbyConfig", BepInDependency.DependencyFlags.SoftDependency)]
    public class PowerfullySlowPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<int> repeatChance { get; set; }
        public static ConfigEntry<bool> frogger;
        internal bool hookSet = false;
        internal static bool froggy = false;
        internal static GameObject frogPrefab = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>("RoR2/Base/moon/FrogInteractable.prefab").WaitForCompletion();

        private void Awake()
        {
            repeatChance = Config.Bind("Configuration", "Repeat Chance", 100, "Percent Chance of runs past first completion repeating old moon. Type:Int,Default:0,Max:100");
            frogger = Config.Bind("Configuration", "Frog, My Dudes", true, "Whether a glass frog is spawned in the boss area after fights on repeat runs of old moon. Does nothing on new moon or on the first, non-repeat, old moon run. Type:Bool,Default:True");

            repeatChance.Value = System.Math.Min(System.Math.Max(repeatChance.Value, 0), 100);
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.KingEnderBrine.InLobbyConfig"))
            {
                HandleLobbyConfigCompat();
            }

            RoR2.Stage.onStageStartGlobal += Stage_onStageStartGlobal;
            Run.onRunStartGlobal += Run_onRunStartGlobal;

            On.RoR2.Achievements.BaseEndingAchievement.OnInstall += BaseEndingAchievement_OnInstall;
            On.RoR2.Achievements.BaseEndingAchievement.OnUninstall += BaseEndingAchievement_OnUninstall;

            On.EntityStates.Missions.BrotherEncounter.PreEncounter.OnExit += PreEncounterOnExit;
            On.EntityStates.Missions.BrotherEncounter.EncounterFinished.OnEnter += AttemptEncounterFinishedOnEnter;

            //Skybox Nullrefs
            On.EntityStates.Missions.BrotherEncounter.Phase1.PreEncounterBegin += Phase1_EnableKarmaSkybox;
            On.EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.UpdateSkyboxColor += UpdateSkyboxReplacement;
            On.EntityStates.Missions.BrotherEncounter.BossDeath.OnEnter += BossDeath_DisableKarmaSkybox;

            //BrotherThrone
            On.EntityStates.Missions.BrotherEncounter.Phase2.PreEncounterBegin += Phase2_BrotherThroneSpawn;

            //Remove TC280s
            On.RoR2.EscapeSequenceController.OnEnable += RemoveMegaDrones;
        }

        private void RemoveMegaDrones(On.RoR2.EscapeSequenceController.orig_OnEnable orig, EscapeSequenceController self)
        {
            orig(self);

            if (IsMoon1())
            {
                var eso = self.transform.Find("EscapeSequenceObjects");
                if (eso)
                {
                    for (int i = 0; i < eso.childCount; i++)
                    {
                        var child = eso.GetChild(i);
                        if (child && child.gameObject.name == "SpawnMegaDroneMaster")
                        {
                            var spawnPoint = child.gameObject.GetComponent<GenericSceneSpawnPoint>();
                            if (spawnPoint)
                            {
                                Destroy(spawnPoint);
                            }
                        }
                    }
                }
            }
        }

        public bool IsMoon1()
        {
            SceneDef sd = SceneCatalog.GetSceneDefForCurrentScene();
            return sd && sd.baseSceneName == "moon";
        }

        private void Phase2_BrotherThroneSpawn(On.EntityStates.Missions.BrotherEncounter.Phase2.orig_PreEncounterBegin orig, EntityStates.Missions.BrotherEncounter.Phase2 self)
        {
            //Check if throne spawnpoint is present
            Transform throneSpawn = self.childLocator.FindChild("ThroneSpawnPoint");
            if (throneSpawn)
            {
                orig(self);
            }
            else if (IsMoon1())
            {
                if (NetworkServer.active)
                {
                    Transform arenaCenter = self.childLocator.FindChild("CenterOfArena");
                    if (arenaCenter)
                    {
                        Vector3 spawnPos = arenaCenter.transform.position + 25f * Vector3.up;

                        DirectorSpawnRequest directorSpawnRequest = new DirectorSpawnRequest(EntityStates.Missions.BrotherEncounter.Phase2.brotherThroneSpawnCard, new DirectorPlacementRule
                        {
                            placementMode = DirectorPlacementRule.PlacementMode.Direct,
                            position = spawnPos
                        }, Run.instance.spawnRng);

                        directorSpawnRequest.teamIndexOverride = new TeamIndex?(TeamIndex.Monster);
                        directorSpawnRequest.ignoreTeamMemberLimit = true;
                        self.brotherThroneInstance = DirectorCore.instance.TrySpawnObject(directorSpawnRequest);
                        CharacterMaster component = self.brotherThroneInstance.GetComponent<CharacterMaster>();
                        if (component)
                        {
                            if (self.speechControllerPrefab)
                            {
                                UnityEngine.Object.Instantiate<GameObject>(self.speechControllerPrefab.gameObject, component.transform).GetComponent<CharacterSpeechController>().characterMaster = component;
                            }
                            component.inventory.GiveItemPermanent(RoR2Content.Items.Karma, 1);
                            component.onBodyStart += delegate (CharacterBody body)
                            {
                                body.AddBuff(RoR2Content.Buffs.BrotherThroneShield);
                            };
                        }
                    }
                }
            }
        }

        private SkyboxReplacementHolder GetSkyboxReplacement(EntityState self, ChildLocator childLocator)
        {
            //Initialize external holder if not present
            SkyboxReplacementHolder srh = self.gameObject.GetComponent<SkyboxReplacementHolder>();
            if (!srh)
            {
                srh = self.gameObject.AddComponent<SkyboxReplacementHolder>();
            }

            //Initialize Skybox Replacement Instance if not present
            if (!srh.skyboxInstance)
            {
                Transform arenaCenter = childLocator.FindChild("CenterOfArena");

                if (arenaCenter)
                {
                    //Skybox Replacement prefab
                    GameObject prefab = Addressables.LoadAssetAsync<GameObject>("3bbc3e3861eca16429c4bd1eaf0954f5").WaitForCompletion();

                    var instance = UnityEngine.Object.Instantiate(prefab);
                    instance.transform.SetParent(arenaCenter, false);

                    instance.transform.localPosition = new Vector3(0f, 33.9f, 0f);
                    instance.transform.localRotation = Quaternion.Euler(270f, 0f, 0f);
                    instance.transform.localScale = new Vector3(109.4335f, 109.4335f, 126.4203f);

                    srh.skyboxInstance = instance;
                    instance.SetActive(false);
                }
            }

            return srh;
        }

        private void Phase1_EnableKarmaSkybox(On.EntityStates.Missions.BrotherEncounter.Phase1.orig_PreEncounterBegin orig, EntityStates.Missions.BrotherEncounter.Phase1 self)
        {
            //This is insanely scuffed
            bool prepareToHandle = false;
            ChildLocator cl = self.GetComponent<ChildLocator>();
            if (IsMoon1() && cl && !cl.FindChild("SkyboxReplacement"))
            {
                prepareToHandle = true;
            }

            if (!prepareToHandle)
            {
                orig(self);
            }
            else
            {
                try
                {
                    orig(self);
                }
                catch (NullReferenceException)
                {
                    Debug.Log("PowerfullySlowMoon: Phase1_EnableKarmaSkybox caught vanilla exception, running our code now!");

                    //Get Skybox Replacement Holder
                    SkyboxReplacementHolder srh = GetSkyboxReplacement(self, cl);

                    //Enable Skybox Replacement if Karma is high enough
                    if (srh && srh.skyboxInstance)
                    {
                        if (KarmaManager.instance.GetEffectiveKarma() >= EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.requiredKarmaForSkyboxChange)
                        {
                            srh.skyboxInstance.SetActive(true);
                        }
                    }

                    //This part is below the place the NRE happens in the vanilla code
                    Transform centerOrb = cl.FindChild("CenterOrbEffect");
                    if (centerOrb)
                    {
                        EffectManager.SpawnEffect(EntityStates.Missions.BrotherEncounter.Phase1.centerOrbDestroyEffect, new EffectData
                        {
                            origin = centerOrb.position
                        }, false);
                    }
                }
            }
        }

        private void UpdateSkyboxReplacement(On.EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.orig_UpdateSkyboxColor orig, EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState self)
        {
            Transform arenaAmbience = self.childLocator.FindChild("SkyboxReplacement");

            if (IsMoon1() && !arenaAmbience)
            {
                //Get Skybox Replacement Holder
                SkyboxReplacementHolder srh = GetSkyboxReplacement(self, self.childLocator);

                //Manually apply skybox stuff here
                if (srh && srh.skyboxInstance)
                {
                    if (srh.skyboxInstance.activeInHierarchy)
                    {
                        var lerpColor = srh.skyboxInstance.GetComponent<LerpTowardsColor>();
                        if (lerpColor)
                        {
                            lerpColor.ResetAndTransitionToNewColor(self.skyboxColor);
                        }
                    }
                }
            }
            else
            {
                orig(self);
            }
        }

        private void BossDeath_DisableKarmaSkybox(On.EntityStates.Missions.BrotherEncounter.BossDeath.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.BossDeath self)
        {
            //This is insanely scuffed
            bool prepareToHandle = false;
            ChildLocator cl = self.GetComponent<ChildLocator>();
            if (IsMoon1() && cl && !cl.FindChild("SkyboxReplacement"))
            {
                prepareToHandle = true;
            }

            if (!prepareToHandle)
            {
                orig(self);
            }
            else
            {
                try
                {
                    orig(self);
                }
                catch (NullReferenceException)
                {
                    Debug.Log("PowerfullySlowMoon: BossDeath_DisableKarmaSkybox caught vanilla exception, running our code now!");
                    
                    //Get Skybox Replacement Holder
                    SkyboxReplacementHolder srh = GetSkyboxReplacement(self, cl);

                    if (srh && srh.skyboxInstance)
                    {
                        srh.skyboxInstance.SetActive(false);
                    }
                }
            }
        }

        private void Run_onRunStartGlobal(Run run)
        {
            SceneCatalog.GetSceneDefFromSceneName("moon").sceneType = SceneType.Stage;
            if (!hookSet && NetworkServer.active && Util.CheckRoll(repeatChance.Value))
            {
                On.EntityStates.LunarTeleporter.Active.OnEnter += EmpowerMoon;
                froggy = frogger.Value;
                void Cleanup(Run run2)
                {
                    On.EntityStates.LunarTeleporter.Active.OnEnter -= EmpowerMoon;
                    Run.onRunDestroyGlobal -= Cleanup;
                    //froggy = false;
                }
                Run.onRunDestroyGlobal += Cleanup;
            }
        }

        private void Stage_onStageStartGlobal(Stage obj)
        {
            if (IsMoon1())
            {
                GameObject shrineRestack = GameObject.Find("ShrineRestack");
                if (shrineRestack)
                {
                    shrineRestack.transform.position = new Vector3(shrineRestack.transform.position.x, 211.8f, shrineRestack.transform.position.z);
                }
            }
        }

        private void BaseEndingAchievement_OnInstall(On.RoR2.Achievements.BaseEndingAchievement.orig_OnInstall orig, RoR2.Achievements.BaseEndingAchievement self)
        {
            orig(self);
            if (self.GetType() == typeof(RoR2.Achievements.CompleteMainEndingAchievement))
            {
                On.EntityStates.LunarTeleporter.Active.OnEnter += EmpowerMoon;
                hookSet = true;
            }
        }

        private void BaseEndingAchievement_OnUninstall(On.RoR2.Achievements.BaseEndingAchievement.orig_OnUninstall orig, RoR2.Achievements.BaseEndingAchievement self)
        {
            orig(self);
            if (self.GetType() == typeof(RoR2.Achievements.CompleteMainEndingAchievement))
            {
                On.EntityStates.LunarTeleporter.Active.OnEnter -= EmpowerMoon;
                hookSet = false;
            }
        }

        private void PreEncounterOnExit(On.EntityStates.Missions.BrotherEncounter.PreEncounter.orig_OnExit orig, EntityStates.Missions.BrotherEncounter.PreEncounter self)
        {
            if (IsMoon1())
            {
                self.childLocator.FindChild("PreEncounter").gameObject.SetActive(false);
            }
            else
            {
                orig(self);
            }
        }

        private void AttemptEncounterFinishedOnEnter(On.EntityStates.Missions.BrotherEncounter.EncounterFinished.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.EncounterFinished self)
        {
            //THE ULTIMATE COPE
            try
            {
                orig(self);
            }
            catch (Exception)
            {
                Debug.Log("PowerfullySlowMoon: AttemptEncounterFinishedOnEnter caught vanilla exception, running our code now!");

                if (IsMoon1())
                {
                    self.childLocator = self.GetComponent<ChildLocator>();
                    if (self.childLocator)
                    {
                        Transform transform = self.childLocator.FindChild("ArenaWalls");
                        Transform transform2 = self.childLocator.FindChild("ArenaNodes");
                        if (transform)
                        {
                            transform.gameObject.SetActive(false);
                        }
                        if (transform2)
                        {
                            transform2.gameObject.SetActive(false);
                        }
                    }

                    if (froggy)
                    {
                        var transform = self.childLocator.FindChild("CenterOrbEffect");
                        Vector3 spawnOffset = new Vector3(0f, 0f, 5f);
                        var fro = GameObject.Instantiate(frogPrefab, transform.position + spawnOffset, Quaternion.identity);
                        var lunColor = PickupCatalog.FindPickupIndex(ItemTier.Lunar);
                        foreach (var high in fro.GetComponentsInChildren<Highlight>())
                        {
                            high.pickupIndex = lunColor;
                            high.highlightColor = Highlight.HighlightColor.pickup;
                            high.isOn = true;
                        }
                        fro.AddComponent<Light>().color = lunColor.GetPickupColor();
                        if (NetworkServer.active)
                        {
                            NetworkServer.Spawn(fro);
                        }
                        EffectManager.SpawnEffect(HealthComponent.AssetReferences.fragileDamageBonusBreakEffectPrefab, new EffectData { origin = transform.position + spawnOffset }, false);
                    }
                }
            }
        }

        internal void EmpowerMoon(On.EntityStates.LunarTeleporter.Active.orig_OnEnter orig, EntityStates.LunarTeleporter.Active self)
        {
            orig(self);
            if (NetworkServer.active)
                self.teleporterInteraction.sceneExitController.destinationScene = SceneCatalog.GetSceneDefFromSceneName("moon");
        }
        internal void HandleLobbyConfigCompat()
        {
            var configEntry = new InLobbyConfig.ModConfigEntry();
            configEntry.DisplayName = "Powerfully Slow";
            configEntry.SectionFields.Add("Old Moon", new List<InLobbyConfig.Fields.IConfigField> { InLobbyConfig.Fields.ConfigFieldUtilities.CreateFromBepInExConfigEntry(repeatChance), InLobbyConfig.Fields.ConfigFieldUtilities.CreateFromBepInExConfigEntry(frogger) });
            InLobbyConfig.ModConfigCatalog.Add(configEntry);
        }
    }

    public class SkyboxReplacementHolder : MonoBehaviour
    {
        public GameObject skyboxInstance;
    }
}