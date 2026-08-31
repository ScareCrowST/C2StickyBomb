using System;
using System.Collections.Generic;
using CommandSystem;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Pickups;
using InventorySystem.Items.ThrowableProjectiles;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using Mirror;
using PlayerRoles;
using UnityEngine;
using Utils;
using MEC;

namespace C2StickyBomb
{
    public class C2StickyBombPlugin : Plugin
    {
        public override string Name => "C2 Sticky Bomb & Sapper";
        public override string Description => "С2, Экипировка сапёра, реалистичный урон и чувствительное обезвреживание.";
        public override string Author => "AI";
        public override Version Version => new Version(1, 0, 9);
        public override Version RequiredApiVersion => new Version(1, 1, 7);

        public const int C2_CUSTOM_ID = 151;
        public const int DETONATOR_CUSTOM_ID = 152;
        public const int SAPPER_CUSTOM_ID = 153;

        public static C2StickyBombPlugin Instance { get; private set; }

        public HashSet<ushort> UnthrownC2Serials = new HashSet<ushort>();
        public Dictionary<ushort, GameObject> ActiveC2Bombs = new Dictionary<ushort, GameObject>();
        public HashSet<ushort> SapperToolkits = new HashSet<ushort>();
        public HashSet<ushort> DefusedDetonators = new HashSet<ushort>();


        public Dictionary<int, float> DefuseHudPause = new Dictionary<int, float>();
        public Dictionary<int, float> HealthBeforeDefuse = new Dictionary<int, float>();
        public Dictionary<int, CoroutineHandle> ActiveDefuseTrackers = new Dictionary<int, CoroutineHandle>();

        private CoroutineHandle _watcherCoroutine;

        public override void Enable()
        {
            Instance = this;
            PlayerEvents.FlippingCoin += OnFlippingCoin;
            PlayerEvents.SearchingPickup += OnSearchingPickup;
            PlayerEvents.UsingItem += OnUsingItem;
            PlayerEvents.UsedItem += OnUsedItem;

            _watcherCoroutine = Timing.RunCoroutine(C2WatcherRoutine());
        }

        public override void Disable()
        {
            PlayerEvents.FlippingCoin -= OnFlippingCoin;
            PlayerEvents.SearchingPickup -= OnSearchingPickup;
            PlayerEvents.UsingItem -= OnUsingItem;
            PlayerEvents.UsedItem -= OnUsedItem;
            Timing.KillCoroutines(_watcherCoroutine);

            foreach (var coroutine in ActiveDefuseTrackers.Values) Timing.KillCoroutines(coroutine);

            UnthrownC2Serials.Clear();
            ActiveC2Bombs.Clear();
            SapperToolkits.Clear();
            DefusedDetonators.Clear();
            DefuseHudPause.Clear();
            HealthBeforeDefuse.Clear();
            ActiveDefuseTrackers.Clear();
            Instance = null;
        }

        private IEnumerator<float> C2WatcherRoutine()
        {
            while (true)
            {
                foreach (var hub in ReferenceHub.AllHubs)
                {
                    var player = LabApi.Features.Wrappers.Player.Get(hub);
                    if (player == null || player.CurrentItem == null) continue;

                    ushort currentSerial = player.CurrentItem.Base.ItemSerial;

                    
                    int pId = player.ReferenceHub.PlayerId;

                    if (UnthrownC2Serials.Contains(currentSerial))
                    {
                        player.SendHint("<align=right><b><color=#ff4444>[ ВЗРЫВЧАТКА C2 ]</color></b>\n<size=20>Бросьте на поверхность для установки</size></align>", 0.6f);
                    }
                    else if (ActiveC2Bombs.ContainsKey(currentSerial))
                    {
                        player.SendHint("<align=right><b><color=#ffcc00>[ ДЕТОНАТОР С2 ]</color></b>\n<size=20>Нажмите ЛКМ для подрыва!</size></align>", 0.6f);
                    }
                    else if (SapperToolkits.Contains(currentSerial))
                    {
                        if (DefuseHudPause.TryGetValue(pId, out float pauseEnd) && Time.time < pauseEnd) continue;

                        player.SendHint("<align=right><b><color=#00aaff>[ НАБОР САПЁРА ]</color></b>\n<size=20>Подойдите к С2 и зажмите ЛКМ</size></align>", 0.6f);
                    }
                }

                if (UnthrownC2Serials.Count > 0)
                {
                    var projectiles = UnityEngine.Object.FindObjectsByType<ThrownProjectile>(UnityEngine.FindObjectsSortMode.None);
                    foreach (var proj in projectiles)
                    {
                        if (UnthrownC2Serials.Contains(proj.NetworkInfo.Serial))
                        {
                            UnthrownC2Serials.Remove(proj.NetworkInfo.Serial);
                            var stickyComp = proj.gameObject.AddComponent<StickyC2Logic>();
                            stickyComp.OwnerHub = proj.PreviousOwner.Hub;
                            stickyComp.C2Serial = proj.NetworkInfo.Serial;
                        }
                    }
                }
                yield return Timing.WaitForSeconds(0.5f);
            }
        }

        private void OnSearchingPickup(PlayerSearchingPickupEventArgs ev)
        {
            if (ev.Pickup != null && ActiveC2Bombs.ContainsValue(ev.Pickup.Base.gameObject))
            {
                ev.IsAllowed = false;
                ev.Player.SendHint("<b><color=red>С2 приклеена намертво!</color></b>", 2f);
            }
        }

        private void OnUsingItem(PlayerUsingItemEventArgs ev)
        {
            if (SapperToolkits.Contains(ev.UsableItem.Base.ItemSerial))
            {
                GameObject closestC2 = GetClosestC2(ev.Player.Position, 3f);
                if (closestC2 != null)
                {
                  
                    int pId = ev.Player.ReferenceHub.PlayerId;

                    DefuseHudPause[pId] = Time.time + 4.5f;
                    HealthBeforeDefuse[pId] = ev.Player.Health;
                    ev.Player.SendHint("<b><color=#00aaff>ОБЕЗВРЕЖИВАНИЕ ЗАПУЩЕНО...</color></b>\n<size=20>Не двигайтесь и не отворачивайтесь!</size>", 4.5f);

                    if (ActiveDefuseTrackers.ContainsKey(pId)) Timing.KillCoroutines(ActiveDefuseTrackers[pId]);
                    ActiveDefuseTrackers[pId] = Timing.RunCoroutine(DefuseMovementTracker(ev.Player, ev.UsableItem.Base.ItemSerial));
                }
                else
                {
                    ev.IsAllowed = false;
                    ev.Player.SendHint("<b><color=red>Вы должны смотреть на С2 и стоять вплотную!</color></b>", 3f);
                    CancelDefuseAnimation(ev.Player, ev.UsableItem.Base.ItemSerial);
                }
            }
        }

        private IEnumerator<float> DefuseMovementTracker(LabApi.Features.Wrappers.Player player, ushort itemSerial)
        {
            yield return Timing.WaitForSeconds(0.2f);

        
            int pId = player.ReferenceHub.PlayerId;
            UnityEngine.Vector3 startPos = player.Position;
            UnityEngine.Quaternion startRot = player.ReferenceHub.PlayerCameraReference.rotation;

            for (int i = 0; i < 40; i++)
            {
                if (player == null || player.CurrentItem == null || player.CurrentItem.Base.ItemSerial != itemSerial)
                    yield break;

                if (UnityEngine.Vector3.Distance(player.Position, startPos) > 0.2f)
                {
                    CancelDefuseWithError(player, itemSerial, "Обезвреживание прервано (вы пошевелились)!");
                    yield break;
                }

                if (UnityEngine.Quaternion.Angle(startRot, player.ReferenceHub.PlayerCameraReference.rotation) > 20f)
                {
                    CancelDefuseWithError(player, itemSerial, "Обезвреживание прервано (вы отвернулись)!");
                    yield break;
                }

                yield return Timing.WaitForSeconds(0.1f);
            }
        }

        private void CancelDefuseWithError(LabApi.Features.Wrappers.Player player, ushort itemSerial, string errorMsg)
        {
            player.SendHint($"<b><color=red>{errorMsg}</color></b>", 3f);
            CancelDefuseAnimation(player, itemSerial);
        }

        private void CancelDefuseAnimation(LabApi.Features.Wrappers.Player player, ushort itemSerial)
        {
           
            int pId = player.ReferenceHub.PlayerId;

            DefuseHudPause[pId] = 0f;
            HealthBeforeDefuse.Remove(pId);

            if (player.CurrentItem != null) player.RemoveItem(player.CurrentItem);

            Timing.CallDelayed(0.1f, () =>
            {
                if (player != null)
                {
                    var newItem = player.AddItem(ItemType.Medkit);
                    SapperToolkits.Remove(itemSerial);
                    SapperToolkits.Add(newItem.Base.ItemSerial);
                    player.CurrentItem = newItem;
                }
            });
        }

        private void OnUsedItem(PlayerUsedItemEventArgs ev)
        {
            if (SapperToolkits.Contains(ev.UsableItem.Base.ItemSerial))
            {
               
                int pId = ev.Player.ReferenceHub.PlayerId;

                if (ActiveDefuseTrackers.ContainsKey(pId))
                {
                    Timing.KillCoroutines(ActiveDefuseTrackers[pId]);
                    ActiveDefuseTrackers.Remove(pId);
                }

                if (HealthBeforeDefuse.TryGetValue(pId, out float oldHealth))
                {
                    if (ev.Player.Health > oldHealth) ev.Player.Health = oldHealth;
                    HealthBeforeDefuse.Remove(pId);
                }

                GameObject closestC2 = GetClosestC2(ev.Player.Position, 3.5f);
                if (closestC2 != null)
                {
                    ushort detonatorSerial = GetDetonatorByC2(closestC2);
                    if (detonatorSerial != 0)
                    {
                        ActiveC2Bombs.Remove(detonatorSerial);
                        DefusedDetonators.Add(detonatorSerial);
                    }

                    NetworkServer.Destroy(closestC2);
                    ev.Player.SendHint("<b><color=#00aaff>С2 УСПЕШНО ОБЕЗВРЕЖЕНА!</color></b>", 4f);
                }
                else
                {
                    ev.Player.SendHint("<b><color=red>С2 исчезла или вы слишком далеко!</color></b>", 3f);
                }

                DefuseHudPause[pId] = 0f;

                Timing.CallDelayed(0.1f, () =>
                {
                    if (ev.Player != null)
                    {
                        var newItem = ev.Player.AddItem(ItemType.Medkit);
                        SapperToolkits.Remove(ev.UsableItem.Base.ItemSerial);
                        SapperToolkits.Add(newItem.Base.ItemSerial);
                        ev.Player.CurrentItem = newItem;
                    }
                });
            }
        }

        private GameObject GetClosestC2(UnityEngine.Vector3 pos, float maxDist)
        {
            GameObject closest = null;
            float minDist = maxDist;
            foreach (var c2 in ActiveC2Bombs.Values)
            {
                if (c2 == null) continue;
                float dist = UnityEngine.Vector3.Distance(pos, c2.transform.position);
                if (dist <= minDist)
                {
                    minDist = dist;
                    closest = c2;
                }
            }
            return closest;
        }

        private ushort GetDetonatorByC2(GameObject c2)
        {
            foreach (var kvp in ActiveC2Bombs)
            {
                if (kvp.Value == c2) return kvp.Key;
            }
            return 0;
        }

        private void OnFlippingCoin(PlayerFlippingCoinEventArgs ev)
        {
            if (ev.Player.CurrentItem == null) return;
            ushort serial = ev.Player.CurrentItem.Base.ItemSerial;

            if (ActiveC2Bombs.TryGetValue(serial, out GameObject c2Object))
            {
                ev.IsAllowed = false;

                if (c2Object != null)
                {
                    UnityEngine.Vector3 explosionPos = c2Object.transform.position;
                    float maxRadius = 7f;
                    float maxDamage = 2000f;

                    ExplosionUtils.ServerSpawnEffect(explosionPos, ItemType.GrenadeHE);
                    Timing.CallDelayed(0.1f, () => ExplosionUtils.ServerSpawnEffect(explosionPos + UnityEngine.Vector3.up, ItemType.GrenadeHE));
                    Timing.CallDelayed(0.2f, () => ExplosionUtils.ServerSpawnEffect(explosionPos + UnityEngine.Vector3.down, ItemType.GrenadeHE));

                    foreach (var hub in ReferenceHub.AllHubs)
                    {
                        var target = LabApi.Features.Wrappers.Player.Get(hub);
                        if (target == null || target.Role == RoleTypeId.Spectator || target.Role == RoleTypeId.None) continue;

                        float distance = UnityEngine.Vector3.Distance(explosionPos, target.Position);
                        if (distance <= maxRadius)
                        {
                            float damage = maxDamage * (1f - (distance / maxRadius));
                            if (damage > 0) target.Damage(damage, "Подорван на C2!");
                        }
                    }

                    var doors = UnityEngine.Object.FindObjectsByType<Interactables.Interobjects.BreakableDoor>(UnityEngine.FindObjectsSortMode.None);
                    foreach (var door in doors)
                    {
                        if (UnityEngine.Vector3.Distance(explosionPos, door.transform.position) <= maxRadius)
                        {
                            door.IsDestroyed = true;
                        }
                    }

                    var windows = UnityEngine.Object.FindObjectsByType<BreakableWindow>(UnityEngine.FindObjectsSortMode.None);
                    foreach (var window in windows)
                    {
                        if (UnityEngine.Vector3.Distance(explosionPos, window.transform.position) <= maxRadius)
                        {
                            window.Damage(500f, null, explosionPos);
                        }
                    }

                    NetworkServer.Destroy(c2Object);
                }

                ActiveC2Bombs.Remove(serial);
                ev.Player.RemoveItem(ev.Player.CurrentItem);
            }
            else if (DefusedDetonators.Contains(serial))
            {
                ev.IsAllowed = false;
                ev.Player.SendHint("<b><color=red>СВЯЗЬ ПОТЕРЯНА!</color></b>\n<size=20>Заряд был обезврежен сапёром</size>", 4f);
                ev.Player.RemoveItem(ev.Player.CurrentItem);
                DefusedDetonators.Remove(serial);
            }
        }
    }

    public class StickyC2Logic : MonoBehaviour
    {
        public ReferenceHub OwnerHub;
        public ushort C2Serial;
        private bool _isStuck = false;

        
        private void OnCollisionEnter(Collision collision)
        {
            if (_isStuck) return;
            _isStuck = true;

            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.detectCollisions = false;
                rb.isKinematic = true;
                rb.linearVelocity = UnityEngine.Vector3.zero;
                rb.angularVelocity = UnityEngine.Vector3.zero;
            }

            Vector3 hitPoint = collision.contacts[0].point;
            Vector3 normal = collision.contacts[0].normal;
            Vector3 spawnPos = hitPoint + (normal * 0.02f);

            if (InventoryItemLoader.TryGetItem(ItemType.GrenadeHE, out ItemBase itemBase))
            {
                ItemPickupBase dummyC2 = UnityEngine.Object.Instantiate(itemBase.PickupDropModel, spawnPos, Quaternion.LookRotation(normal));
                dummyC2.NetworkInfo = new PickupSyncInfo(ItemType.GrenadeHE, itemBase.Weight, C2Serial);
                NetworkServer.Spawn(dummyC2.gameObject);

                dummyC2.GetComponent<Rigidbody>().isKinematic = true;

               
                dummyC2.transform.SetParent(collision.collider.transform, true);

                if (OwnerHub != null)
                {
                    var player = LabApi.Features.Wrappers.Player.Get(OwnerHub);
                    if (player != null)
                    {
                        var detonator = player.AddItem(ItemType.Coin);
                        C2StickyBombPlugin.Instance.ActiveC2Bombs[detonator.Base.ItemSerial] = dummyC2.gameObject;
                        player.SendHint("<b><color=#ffcc00>С2 УСТАНОВЛЕНА!</color>\nИспользуйте Детонатор для подрыва.</b>", 5f);
                    }
                }
            }

            NetworkServer.Destroy(this.gameObject);
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public class GiveC2Command : CommandSystem.ICommand
    {
        public string Command => "givec2";
        public string[] Aliases => new[] { "c2" };
        public string Description => "Выдает бомбу-липучку С2";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (LabApi.Features.Wrappers.Player.TryGet(sender, out var player))
            {
                var item = player.AddItem(ItemType.GrenadeHE);
                C2StickyBombPlugin.Instance.UnthrownC2Serials.Add(item.Base.ItemSerial);
                response = $"С2 успешно выдана! (Custom ID: {C2StickyBombPlugin.C2_CUSTOM_ID})";
                return true;
            }
            response = "Эту команду может использовать только игрок.";
            return false;
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public class GiveSapperCommand : CommandSystem.ICommand
    {
        public string Command => "givesapper";
        public string[] Aliases => new[] { "sapper" };
        public string Description => "Выдает набор сапёра для обезвреживания С2";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (LabApi.Features.Wrappers.Player.TryGet(sender, out var player))
            {
                var item = player.AddItem(ItemType.Medkit);
                C2StickyBombPlugin.Instance.SapperToolkits.Add(item.Base.ItemSerial);
                response = $"Набор сапёра выдан! (Custom ID: {C2StickyBombPlugin.SAPPER_CUSTOM_ID})";
                return true;
            }
            response = "Эту команду может использовать только игрок.";
            return false;
        }
    }
}