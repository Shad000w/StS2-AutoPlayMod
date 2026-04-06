using AutoPlay.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;
using System.Text.Json;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace AutoPlay;

public static class ModSettings
{
	public static bool AutomaticDiscard { get; set; } = true;
	public static bool AutomaticExhaust { get; set; } = true;
	public static bool AutomaticSelect { get; set; } = true;
	public static bool AutomaticRetain { get; set; } = true;
	public static bool HardSelect { get; set; } = true;
}
public class AutoPlayConfigData
{
	public bool AutomaticDiscard { get; set; } = true;
	public bool AutomaticExhaust { get; set; } = true;
	public bool AutomaticSelect { get; set; } = true;
	public bool AutomaticRetain { get; set; } = true;
	public bool HardSelect { get; set; } = true;
}

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
	private const string ModId = "AutoPlay";

	private static Logger Logger { get; } = new(ModId, LogType.Generic);

	public static void Initialize()
	{
		Harmony harmony = new(ModId);
		harmony.PatchAll();

		if (File.Exists("mods/AutoPlay.json"))
		{
			var json = File.ReadAllText("mods/AutoPlay.json");
			var data = JsonSerializer.Deserialize<AutoPlayConfigData>(json);

			if (data != null)
			{
				ModSettings.AutomaticDiscard = data.AutomaticDiscard;
				ModSettings.AutomaticExhaust = data.AutomaticExhaust;
				ModSettings.AutomaticSelect = data.AutomaticSelect;
				ModSettings.AutomaticRetain = data.AutomaticRetain;
				ModSettings.HardSelect = data.HardSelect;
			}
		}

		ModConfigBridge.DeferredRegister();

		Log("Initialized");
	}

	public static void Log(string message, LogLevel level = LogLevel.Info, int skipFrames = 1)
	{
		Logger.LogMessage(level, $"[{ModId}] {message}", skipFrames);

		try
		{
			var console = NDevConsole.Instance;
			var outputBufferField =
				typeof(NDevConsole).GetField("_outputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
			if (outputBufferField?.GetValue(console) is not RichTextLabel outputBuffer) return;
			outputBuffer.Text += $"[color=#00ffff][{ModId}][/color] {message}";
			outputBuffer.Text += "\n";
		}
		catch
		{
			// Console might not be initialized yet
		}
	}
}

[HarmonyPatch]
public class Patch
{
	[HarmonyPatch(typeof(NCombatUi), "_Input")]
	[HarmonyPostfix]
	private static void GameInputHook(InputEvent inputEvent)
	{
		if (!ModSettings.HardSelect || inputEvent is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo || keyEvent.Keycode != Key.Tab || keyEvent.AltPressed || keyEvent.CtrlPressed || NDevConsole.Instance.Visible) return;

		if (NPlayerHand.Instance?.InCardPlay != false || NTargetManager.Instance.IsInSelection || CombatManager.Instance.IsOverOrEnding || NCombatRoom.Instance == null || ModState.TargettedEnemy == null || ModState.TargettedEnemy.Entity.CombatState == null) return;

		var enemies = ModState.TargettedEnemy.Entity.CombatState.HittableEnemies;
		int index = enemies.IndexOf(ModState.TargettedEnemy.Entity);

		if (index >= 0 && enemies.Count > 0)
		{
			for (int th = 1; th <= enemies.Count; th++)
			{
				int nextIndex = (keyEvent.ShiftPressed ? (index - th + enemies.Count) : (index + th)) % enemies.Count;
				var nextEnemy = enemies[nextIndex];

				if (nextEnemy.IsAlive)
				{
					var enemy_node = NCombatRoom.Instance.GetCreatureNode(nextEnemy);
					if (enemy_node != null)
					{
						ModState.DoNotHideReticle = false;
						ModState.TargettedEnemy.HideSingleSelectReticle();
						ModState.TargettedEnemy = enemy_node;
						ModState.TargettedEnemy.ShowSingleSelectReticle();
						ModState.DoNotHideReticle = true;
					}
					break;
				}
			}
		}
	}

	[HarmonyPatch(typeof(NCreatureVisuals), "_Ready")]
	[HarmonyPostfix]
	private static void NCreatureVisuals_Ready(NCreatureVisuals __instance)
	{
		var parent = __instance.GetParent<NCreature>();


		if (parent?.Hitbox != null && parent.Entity.IsMonster && !parent.Entity.IsPet && parent.Entity.IsAlive)
		{
			parent.Hitbox.GuiInput += (inputEvent) =>
			{
				if ((inputEvent is InputEventMouseButton mouse && mouse.Pressed && !mouse.DoubleClick && mouse.ButtonIndex == MouseButton.Left) || (inputEvent is InputEventScreenTouch touch && touch.Pressed && !touch.DoubleTap))
				{
					if (NTargetManager.Instance.IsInSelection || CombatManager.Instance.IsOverOrEnding || Time.GetTicksMsec() < ModState.IgnoreEnemyClickUntilMs) return;

					if (ModState.DoNotHideReticle != true)
					{
						ModState.TargettedEnemy?.HideSingleSelectReticle();
						parent.ShowSingleSelectReticle();
						ModState.DoNotHideReticle = true;
						ModState.TargettedEnemy = parent;
					}
					else
					{
						ModState.DoNotHideReticle = false;
						ModState.TargettedEnemy?.HideSingleSelectReticle();
						if (parent != ModState.TargettedEnemy)
						{
							parent.ShowSingleSelectReticle();
							ModState.DoNotHideReticle = true;
							ModState.TargettedEnemy = parent;
						}
						else
						{
							ModState.TargettedEnemy = null;
						}
					}
				}
			};
		}
	}

	[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom._Ready))]
	[HarmonyPostfix]
	private static void CombatRoomEnter(NCombatRoom __instance)
	{
		if (__instance != null && __instance.Mode == CombatRoomMode.ActiveCombat && ModState.TargettedEnemy != null)
		{
			ModState.DoNotHideReticle = false;
			ModState.TargettedEnemy.HideSingleSelectReticle();
			ModState.TargettedEnemy = null;
		}
	}

	[HarmonyPatch(typeof(NCreature), nameof(NCreature.HideSingleSelectReticle))]
	[HarmonyPrefix]
	private static bool HideSingleSelectReticle(NCreature __instance)
	{
		return !ModSettings.HardSelect || ModState.TargettedEnemy != __instance || !ModState.DoNotHideReticle;
	}

	[HarmonyPatch(typeof(NCreature), "StartDeathAnim")]
	[HarmonyPostfix]
	private static void CreatureStartDeathAnim(NCreature __instance)
	{
		if (ModState.TargettedEnemy != null && ModState.TargettedEnemy == __instance)
		{
			ModState.DoNotHideReticle = false;
			ModState.TargettedEnemy.HideSingleSelectReticle();
			ModState.TargettedEnemy = null;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(NMouseCardPlay), "TargetSelection")]
	private static bool TargetSelection(NMouseCardPlay __instance, TargetMode targetMode, ref Task __result)
	{
		var card = AccessTools.PropertyGetter(typeof(NCardPlay), "Card")?.Invoke(__instance, null) as CardModel;

		if (!IsAutoPlayable(card)) return true;

		// manually set _target if type == AnyEnemy
		if (card is { TargetType: TargetType.AnyEnemy })
		{
			var target = card.CombatState?.HittableEnemies[0];
			if (target is null) return true;
			else if(ModSettings.HardSelect && ModState.TargettedEnemy != null && !ModState.TargettedEnemy.Entity.IsDead)
			{
				target = ModState.TargettedEnemy.Entity;
			}

			AccessTools.Field(typeof(NMouseCardPlay), "_target").SetValue(__instance, target);
		}

		// MegaCrit sets _target for All other types in IsAutoPlayable()
		__result = Task.CompletedTask;
		return false;
	}


	[HarmonyPrefix]
	[HarmonyPatch(typeof(NMouseCardPlay), "IsCardInPlayZone")]
	private static bool IsCardInPlayZone(ref bool __result)
	{
		__result = true;
		return false;
	}

	[HarmonyPatch(typeof(NPlayerHand), "StartCardPlay")]
	private static void StartCardPlay(NHandCardHolder holder, ref bool startedViaShortcut)
	{
		if (IsAutoPlayable(holder.CardModel)) startedViaShortcut = true;
	}

	[HarmonyPatch(typeof(NHandCardHolder), "OnFocus")]
	[HarmonyPostfix]
	private static void CardOnFocus(NHandCardHolder __instance)
	{
		if (__instance == null || __instance.CardModel == null || __instance?.CardModel.CombatState == null) return;

		if (__instance.CardModel.TargetType == TargetType.AnyEnemy && IsAutoPlayable(__instance.CardModel))
		{
			var target = __instance.CardModel.CombatState.HittableEnemies[0];
			if (target is null) return;
			else if (ModSettings.HardSelect && ModState.TargettedEnemy != null && !ModState.TargettedEnemy.Entity.IsDead)
			{
				target = ModState.TargettedEnemy.Entity;
			}

			__instance.CardNode?.SetPreviewTarget(target);
		}
		else
		{
			int num_enemies = 0, num_enemies_with_same_damage_modifiers = 0;
			Creature? first = null;
			foreach (Creature enemy in __instance.CardModel.CombatState.HittableEnemies)
			{ 
				if(enemy.IsAlive)
				{
					num_enemies++;
					if (enemy.HasPower<VulnerablePower>() || enemy.HasPower<HardToKillPower>() || enemy.HasPower<FlutterPower>() || enemy.HasPower<SlipperyPower>() )
					{						
						if (first == null)
						{
							first = enemy;
							num_enemies_with_same_damage_modifiers++;
						}
						else if(enemy.HasPower<SlipperyPower>() == first.HasPower<SlipperyPower>() && enemy.HasPower<VulnerablePower>() == first.HasPower<VulnerablePower>() && enemy.GetPowerAmount<HardToKillPower>() == first.GetPowerAmount<HardToKillPower>() && enemy.HasPower<FlutterPower>() == first.HasPower<FlutterPower>())
						{
							num_enemies_with_same_damage_modifiers++;
						}						
					}
				}
			}
			if (num_enemies_with_same_damage_modifiers > 0 && num_enemies_with_same_damage_modifiers == num_enemies)
			{
				__instance.CardNode?.SetPreviewTarget(first);
			}
			else
			{
				__instance?.CardNode?.SetPreviewTarget(null);
			}
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(NPotionHolder), "OnRelease")]
	private static bool OnPotionClicked(NPotionHolder __instance)
	{
		if (__instance?.Potion?.Model.Owner.Creature.CombatState == null || CombatManager.Instance.IsOverOrEnding) return true;

		var isUsable_field = AccessTools.Field(typeof(NPotionHolder), "_isUsable");

		var isUsable_value = isUsable_field.GetValue(__instance);

		if (isUsable_value == null || (bool)isUsable_value == false) return true;

		if (__instance.Potion.Model.Usage != PotionUsage.Automatic && __instance.Potion.Model.Id.Entry != "FOUL_POTION")//automatically use every potion except Foul Potion when clicked at them in combat since there is no reason to discard
		{
			__instance.UsePotion();
			return false;
		}
		return true;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(NPotionHolder), "UsePotion")]
	private static bool UsePotion(NPotionHolder __instance, ref Task __result)
	{
		PotionModel? potion = __instance.Potion?.Model;
		if (potion == null || potion.Usage == PotionUsage.Automatic) return true;

		if (!IsAutoPlayable(potion)) return true;

		var target = potion.Owner.Creature.CombatState?.HittableEnemies[0];
		if (target is null) return true;
		else if (ModSettings.HardSelect && ModState.TargettedEnemy != null && !ModState.TargettedEnemy.Entity.IsDead)
		{
			target = ModState.TargettedEnemy.Entity;
		}

		potion.EnqueueManualUse(target);

		__result = Task.CompletedTask;
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(WellLaidPlansPower), "BeforeFlushLate")]
	private static bool BeforeFlushLate(WellLaidPlansPower __instance, PlayerChoiceContext choiceContext, Player player, ref Task __result)
	{
		if (!ModSettings.AutomaticRetain) return true;
		if (player != __instance.Owner.Player) return true;

		CardPile handPile = PileType.Hand.GetPile(__instance.Owner.Player);

		if (handPile.Cards.Count == 0) return true;
		else if (handPile.Cards.Count <= __instance.Amount)//if the number of remaining cards is same or lower than required amount to choose, choose them all automatically, unless they are unplayable
		{
			foreach (CardModel item in handPile.Cards)
			{
				if ((item.Type <= CardType.Power || item.Id.Entry == "FRANTIC_ESCAPE") && !item.Keywords.Contains(CardKeyword.Retain))
				{
					item.GiveSingleTurnRetain();
				}
			}

			__result = Task.CompletedTask;
			return false;
		}

		CardModel first = handPile.Cards[0];

		for (int th = 0; th < handPile.Cards.Count; th++)
		{
			if (!CardsEqual(first, handPile.Cards[th]))
			{
				return true;
			}
		}

		//if all remaining cards are identical, it will retain required amount automatically

		int num_to_retain = __instance.Amount;

		for (int th = 0; th < handPile.Cards.Count && num_to_retain > 0; th++)
		{
			CardModel card = handPile.Cards[th];
			if ((card.Type <= CardType.Power || card.Id.Entry == "FRANTIC_ESCAPE") && !card.Keywords.Contains(CardKeyword.Retain))
			{
				card.GiveSingleTurnRetain();
				num_to_retain--;
			}
		}

		__result = Task.CompletedTask;
		return false;
	}

	[HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
	[HarmonyPostfix]
	private static void AfterCardPlayed(CardModel __instance, ref Task __result)
	{
		__result = AfterCardPlayedFinished(__result, __instance);
	}

	private static async Task AfterCardPlayedFinished(Task originalTask, CardModel __instance)
	{
		ModState.IgnoreEnemyClickUntilMs = Time.GetTicksMsec() + 250;
		await originalTask;

		if (CombatManager.Instance.IsPlayPhase)
		{
			AutomaticEndTurn(__instance.Owner);
		}
	}

	[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
	[HarmonyPostfix]
	private static void AfterPotionUsed(PotionModel potion)
	{
		AutomaticEndTurn(potion.Owner);
	}

	[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionDiscarded))]
	[HarmonyPostfix]
	private static void AfterPotionDiscarded(PotionModel potion)
	{
		if (CombatManager.Instance.IsPlayPhase)
		{
			AutomaticEndTurn(potion.Owner);
		}
	}

	[HarmonyPatch(typeof(Creature), nameof(Creature.AfterTurnStart))]
	[HarmonyPostfix]
	private static void AfterTurnStart(Creature __instance, int roundNumber, CombatSide side)
	{
		if (__instance.CombatState != null && __instance.IsPlayer && __instance.Player != null && roundNumber > 0 && side != CombatSide.Enemy && LocalContext.NetId == __instance.Player.NetId)
		{
			int num_enemies_survive_this_turn = 0;

			int damage_from_bomb = 0;

			IReadOnlyList<Creature> allies = __instance.CombatState.PlayerCreatures;
			for (int th = 0; th < allies.Count; th++)
			{
				Creature ally = allies[th];
				var bombPower = ally.GetPower<TheBombPower>();
				if (bombPower != null && bombPower.Amount == 1)
				{
					damage_from_bomb += bombPower.DynamicVars.Damage.IntValue;
				}
			}

			IReadOnlyList<Creature> enemies = __instance.CombatState.Enemies;
			for (int th = 0; th < enemies.Count; th++)
			{
				Creature enemy = enemies[th];
				int damage_from_poison = (enemy.GetPower<PoisonPower>()?.CalculateTotalDamageNextTurn() ?? 0);

				if (enemy.IsAlive && (enemy.GetPowerAmount<ArtifactPower>() > 0 || enemy.GetPowerAmount<InfestedPower>() > 0 || enemy.GetPowerAmount<SteamEruptionPower>() > 0 || enemy.GetPowerAmount<AdaptablePower>() > 0 || damage_from_poison + damage_from_bomb < enemy.CurrentHp))
				{
					num_enemies_survive_this_turn++;
				}
			}

			CardPile handPile = PileType.Hand.GetPile(__instance.Player);

			if (num_enemies_survive_this_turn == 0)//no enemies will survive after turn started
			{
				int num_damage_received_from_cards = 0;
				bool has_feed_card = false;

				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if (card.Id.Entry == "FEED")
					{
						has_feed_card = true;
					}
					else if (card.Type > CardType.Power)
					{
						num_damage_received_from_cards += card.DynamicVars.Damage.IntValue;
					}
				}

				if (has_feed_card == false && num_damage_received_from_cards <= __instance.Block + __instance.GetPowerAmount<PlatingPower>())
				{
					//we either have no damage debuffs or we can block them so no reason to play this round further
					RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(__instance.Player, __instance.CombatState.RoundNumber));
					return;
				}
			}
		}
	}

	[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
	public static class Patch_SelectCards
	{
		[HarmonyPrefix]
		private static bool Prefix(NPlayerHand __instance, CardSelectorPrefs prefs, Func<CardModel, bool> filter, AbstractModel source, NPlayerHand.Mode mode, ref Task<IEnumerable<CardModel>> __result)
		{
			if (ModState.CurrentPlayer == null) return true;

			bool IsDiscardTypeOfSelect = prefs.Prompt.LocEntryKey == "TO_DISCARD";
			bool IsExhaustTypeOfSelect = prefs.Prompt.LocEntryKey == "TO_EXHAUST";

			if (IsDiscardTypeOfSelect && !ModSettings.AutomaticDiscard) return true;
			else if (IsExhaustTypeOfSelect && !ModSettings.AutomaticExhaust) return true;
			else if (!IsExhaustTypeOfSelect && !IsDiscardTypeOfSelect && !ModSettings.AutomaticSelect) return true;

			CardPile handPile = PileType.Hand.GetPile(ModState.CurrentPlayer);

			if (handPile.Cards.Count == 0) return true;//if we have no cards, game already selects nothing by default, so we can leave it to original function
			else if (prefs.MaxSelect == 999999999) return true;//Gambler's Brew potion
			else if (prefs.RequireManualConfirmation || (!IsDiscardTypeOfSelect && prefs.MinSelect == 0)) return true;

			var selected = new List<CardModel> { };

			if (handPile.Cards.Count <= prefs.MinSelect)//if we have less cards than amount needed to select, select them automatically
			{
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					selected.Add(handPile.Cards[th]);
				}
			}
			else if (IsDiscardTypeOfSelect)//discard selection
			{
				CardModel? first_optimal_card__to_discard = null;
				bool all_cards_to_discard_optimally_identical = true;
				int num_sly_cards = 0, num_ethereal_cards_that_cannot_be_played = 0, num_unplayable_cards = 0, num_playable_cards_with_exhaust_power = 0;
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if (card.IsSlyThisTurn)
					{
						num_sly_cards++;
						if (first_optimal_card__to_discard == null) first_optimal_card__to_discard = card;
						else if (!CardsEqual(first_optimal_card__to_discard, card))
						{
							all_cards_to_discard_optimally_identical = false;
						}
					}
					else if (card.Type > CardType.Power && card.Id.Entry != "FRANTIC_ESCAPE")
					{
						num_unplayable_cards++;
						if (first_optimal_card__to_discard == null) first_optimal_card__to_discard = card;
						else if (!CardsEqual(first_optimal_card__to_discard, card))
						{
							all_cards_to_discard_optimally_identical = false;
						}
					}
					else
					{
						if ((card.Id.Entry == "BRAND" || card.Id.Entry == "BURNING_PACT" || card.Id.Entry == "SCAVENGE" || card.Id.Entry == "FLAK_CANNON" || card.Id.Entry == "SECOND_WIND" || card.Id.Entry == "PURITY") && card.CanPlay())
						{
							num_playable_cards_with_exhaust_power++;
						}
						else if(card.Keywords.Contains(CardKeyword.Ethereal) && !card.CanPlay())
						{
							num_ethereal_cards_that_cannot_be_played++;
							if (first_optimal_card__to_discard == null) first_optimal_card__to_discard = card;
							else if (!CardsEqual(first_optimal_card__to_discard, card))
							{
								all_cards_to_discard_optimally_identical = false;
							}
						}
					}
				}

				int num_cards_to_discard_optimally = num_sly_cards + num_ethereal_cards_that_cannot_be_played;

				if (num_playable_cards_with_exhaust_power == 0)//only consider unplayable cards if we can't exhaust them this round
				{
					num_cards_to_discard_optimally += num_unplayable_cards;
				}


				if (num_cards_to_discard_optimally == prefs.MinSelect)//if there is only one Sly or unplayable card it will discard it automatically - should be always the most optimal choice
				{
					for (int th = 0; th < handPile.Cards.Count && num_cards_to_discard_optimally > 0; th++)
					{
						CardModel card = handPile.Cards[th];
						if (card.IsSlyThisTurn || (num_playable_cards_with_exhaust_power == 0 && (card.Type > CardType.Power && card.Id.Entry != "FRANTIC_ESCAPE")) || (card.Keywords.Contains(CardKeyword.Ethereal) && !card.CanPlay()))
						{
							selected.Add(card);
							num_cards_to_discard_optimally--;
						}
					}
				}
				else if (num_cards_to_discard_optimally > prefs.MinSelect && all_cards_to_discard_optimally_identical)//if all optimally discardable cards are same, discard required amount automatically
				{
					int num_to_discard = prefs.MinSelect;
					for (int th = 0; th < handPile.Cards.Count && num_to_discard > 0; th++)
					{
						CardModel card = handPile.Cards[th];
						if (card.IsSlyThisTurn || (num_playable_cards_with_exhaust_power == 0 && (card.Type > CardType.Power && card.Id.Entry != "FRANTIC_ESCAPE")) || (card.Keywords.Contains(CardKeyword.Ethereal) && !card.CanPlay()))
						{
							selected.Add(card);
							num_to_discard--;
						}
					}
				}
				else
				{
					CardModel first = handPile.Cards[0];

					for (int th = 1; th < handPile.Cards.Count; th++)
					{
						if (!CardsEqual(first, handPile.Cards[th]))
						{
							return true;
						}
					}

					for (int th = 0; th < prefs.MinSelect; th++)
					{
						selected.Add(handPile.Cards[th]);
					}
				}
			}
			else if (IsExhaustTypeOfSelect)
			{
				CardModel? first_optimal_card__to_exhaust = null;
				bool all_cards_to_exhaust_optimally_identical = true;
				int num_cards_to_exhaust_optimally = 0;
				for (int th = 0; th < handPile.Cards.Count; th++)
				{
					CardModel card = handPile.Cards[th];
					if ((card.Type > CardType.Power && card.Id.Entry != "FRANTIC_ESCAPE") || (card.Keywords.Contains(CardKeyword.Ethereal) && !card.CanPlay()))
					{
						num_cards_to_exhaust_optimally++;
						if (first_optimal_card__to_exhaust == null) first_optimal_card__to_exhaust = card;
						else if (!CardsEqual(first_optimal_card__to_exhaust, card))
						{
							all_cards_to_exhaust_optimally_identical = false;
						}
					}
				}

				if (num_cards_to_exhaust_optimally == prefs.MinSelect)//if there is only one Sly or unplayable card it will discard it automatically - should be always the most optimal choice
				{
					for (int th = 0; th < handPile.Cards.Count && num_cards_to_exhaust_optimally > 0; th++)
					{
						CardModel card = handPile.Cards[th];
						if ((card.Type > CardType.Power && card.Id.Entry != "FRANTIC_ESCAPE") || (card.Keywords.Contains(CardKeyword.Ethereal) && !card.CanPlay()))
						{
							selected.Add(card);
							num_cards_to_exhaust_optimally--;
						}
					}
				}
				else if (num_cards_to_exhaust_optimally > prefs.MinSelect && all_cards_to_exhaust_optimally_identical)//if all optimally discardable cards are same, discard required amount automatically
				{
					int num_to_discard = prefs.MinSelect;
					for (int th = 0; th < handPile.Cards.Count && num_to_discard > 0; th++)
					{
						CardModel card = handPile.Cards[th];
						if ((card.Type > CardType.Power && card.Id.Entry != "FRANTIC_ESCAPE") || (card.Keywords.Contains(CardKeyword.Ethereal) && !card.CanPlay()))
						{
							selected.Add(card);
							num_to_discard--;
						}
					}
				}
				else
				{
					CardModel first = handPile.Cards[0];
					for (int th = 1; th < handPile.Cards.Count; th++)
					{
						if (!CardsEqual(first, handPile.Cards[th]))
						{
							return true;
						}
					}

					for (int th = 0; th < prefs.MinSelect; th++)
					{
						selected.Add(handPile.Cards[th]);
					}
				}
			}
			else//not discard nor exhaust selection - only select automatically when all cards in hand are identical
			{
				CardModel first = handPile.Cards[0];
				for (int th = 1; th < handPile.Cards.Count; th++)
				{
					if (!CardsEqual(first, handPile.Cards[th]))
					{
						return true;
					}
				}

				for (int th = 0; th < prefs.MinSelect; th++)
				{
					selected.Add(handPile.Cards[th]);
				}
			}

			__result = Task.FromResult<IEnumerable<CardModel>>(selected);
			return false;
		}
	}

	[HarmonyPatch(typeof(NPlayerHand), "SelectCardInSimpleMode")]
	[HarmonyPostfix]
	private static void SelectCardInSimpleMode(NPlayerHand __instance)
	{
		var prefsField = Traverse.Create(__instance).Field<CardSelectorPrefs>("_prefs");
		if (prefsField == null) return;
		var prefs = prefsField.Value;

		var selectedCards = Traverse.Create(__instance).Field<List<CardModel>>("_selectedCards").Value;
		int selectedCount = selectedCards?.Count ?? 0;

		if (prefs.MaxSelect < 1) return;
		else if (selectedCards == null || selectedCount != prefs.MaxSelect) return;

		// Press the confirm button to complete the selection
		var confirmMethod = AccessTools.Method(__instance.GetType(), "OnSelectModeConfirmButtonPressed");
		if (confirmMethod != null)
		{
			confirmMethod.Invoke(__instance, new object[] { null! });
		}
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
	[HarmonyPrefix]
	private static void FromHand(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
	{
		ModState.CurrentPlayer = player;
	}

	[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
	[HarmonyPrefix]
	private static bool FromSimpleGrid(PlayerChoiceContext context, IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs, ref Task<IEnumerable<CardModel>> __result)
	{
		if (!ModSettings.AutomaticDiscard) return true;
		if (CombatManager.Instance.IsEnding) return true;

		List<CardModel> cards = cardsIn.ToList();
		if (!prefs.RequireManualConfirmation && cards.Count <= prefs.MinSelect)//if we have less cards than needed to select fall back to original function (it selects automatically)
		{
			return true;
		}

		CardModel first = cards[0];
		for (int th = 1; th < cards.Count; th++)
		{
			if (!CardsEqual(first, cards[th]))
			{
				return true;
			}
		}

		//if all remaining cards are identical, it will select required amount automatically
		var selected = new List<CardModel> { };
		for (int th = 0; th < prefs.MinSelect; th++)
		{
			selected.Add(cards[th]);
		}

		__result = Task.FromResult<IEnumerable<CardModel>>(selected);
		return false;
	}

	private static void AutomaticEndTurn(Player player)
	{
		if (CombatManager.Instance.IsOverOrEnding || player.Creature.CombatState == null || LocalContext.NetId != player.NetId) return;

		int damage_from_bomb = 0;

		IReadOnlyList<Creature> allies = player.Creature.CombatState.PlayerCreatures;
		for(int th = 0; th < allies.Count; th++)
		{
			Creature ally = allies[th];
			var bombPower = ally.GetPower<TheBombPower>();
			if (bombPower != null && bombPower.Amount == 1)
			{
				damage_from_bomb+= bombPower.DynamicVars.Damage.IntValue;
			}
		}

		int num_enemies_survive_this_turn = 0, num_enemies_intends_attack = 0, num_weak_enemies = 0, num_enemy_damage = 0, num_damage_received_from_cards = 0, minimum_enemy_hitpoints = 999;

		IReadOnlyList<Creature> enemies = player.Creature.CombatState.Enemies;
		for (int th = 0; th < enemies.Count; th++)
		{
			Creature enemy = enemies[th];
			int damage_from_poison = (enemy.GetPower<PoisonPower>()?.CalculateTotalDamageNextTurn() ?? 0);

			if (enemy.IsAlive && (enemy.GetPowerAmount<ArtifactPower>() > 0 || enemy.GetPowerAmount<InfestedPower>() > 0 || enemy.GetPowerAmount<SteamEruptionPower>() > 0 || enemy.GetPowerAmount<AdaptablePower>() > 0 || damage_from_poison+damage_from_bomb < enemy.CurrentHp))
			{
				num_enemies_survive_this_turn++;

				if (enemy.Monster?.IntendsToAttack == true)
				{
					num_enemies_intends_attack++;
					if (enemy.GetPowerAmount<WeakPower>() > 0)
					{
						num_weak_enemies++;
					}
					foreach (var intent in enemy.Monster.NextMove.Intents)
					{
						if (intent is AttackIntent attackIntent)
						{
							num_enemy_damage += attackIntent.GetTotalDamage(allies, owner: enemy);
						}
					}
				}
				int hp_after_poison = enemy.CurrentHp - damage_from_poison - damage_from_bomb - enemy.GetPowerAmount<PlowPower>() - enemy.GetPowerAmount<ShriekPower>() + enemy.Block;
				if (hp_after_poison < minimum_enemy_hitpoints)
				{
					minimum_enemy_hitpoints = hp_after_poison;
				}
			}
		}

		CardPile handPile = PileType.Hand.GetPile(player);

		if (num_enemies_survive_this_turn == 0)//no enemies will survive after this card was played
		{
			bool has_feed_card = false;

			for (int th = 0; th < handPile.Cards.Count; th++)
			{
				CardModel card = handPile.Cards[th];
				if (card.Id.Entry == "FEED")
				{
					has_feed_card = true;
				}
				else if (card.Type > CardType.Power)
				{
					num_damage_received_from_cards += card.DynamicVars.Damage.IntValue;
				}
			}

			if (has_feed_card == false && num_damage_received_from_cards <= player.Creature.Block + player.Creature.GetPowerAmount<PlatingPower>())
			{
				//we either have no damage debuffs or we can block them so no reason to play this round further
				RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.Creature.CombatState.RoundNumber));
				return;
			}
		}

		int num_playable_cards = 0, num_upgradable_cards = 0, num_cards_using_energy = 0, num_cards_using_stars = 0, minimum_energy_needed_to_play_card = 99, minimum_stars_needed_to_play_card = 99;

		for (int th = 0; th < handPile.Cards.Count; th++)
		{
			CardModel card = handPile.Cards[th];
			int card_star_cost = card.GetStarCostWithModifiers();
			int card_energy_cost = card.EnergyCost.GetWithModifiers(CostModifiers.All);
			//Log.Info(th + ". card: " + card.Title + " card_type: " + card.Type + " card_star_cost: " + card_star_cost + ", card_energy_cost: " + card_energy_cost);

			if (card.CanPlay(out UnplayableReason reason, preventer: out _))
			{
				return;
			}
			else if (card.Type <= CardType.Power)
			{
				num_playable_cards++;
			}
			if (card.IsUpgradable)
			{
				num_upgradable_cards++;
			}
			if (card_energy_cost > 0 && reason == UnplayableReason.EnergyCostTooHigh)
			{
				num_cards_using_energy++;
				if (card_energy_cost < minimum_energy_needed_to_play_card)
				{
					minimum_energy_needed_to_play_card = card_energy_cost;
				}
			}
			if (card_star_cost > 0 && reason == UnplayableReason.StarCostTooHigh)
			{
				num_cards_using_stars++;
				if (card_star_cost < minimum_stars_needed_to_play_card)
				{
					minimum_stars_needed_to_play_card = card_star_cost;
				}
			}
		}

		CardPile drawPile = PileType.Draw.GetPile(player);
		CardPile discardPile = PileType.Discard.GetPile(player);
		int total_damage_from_potions = 0;

		//if we got here, we have no cards to play or no energy to play them
		foreach (PotionModel another_potion in player.Potions)
		{
			string id = another_potion.Id.Entry;
			if (another_potion.Usage == PotionUsage.Automatic)//automatically used potions can be safely ignored
			{
				//ignore
			}
			else if (id == "GIGANTIFICATION_POTION" || id == "SOLDIERS_STEW" || id == "DUPLICATOR")//if we have no playable attack cards in hand, then potions that improve attack cards are useless
			{
				//ignore
			}
			else if (another_potion.TargetType == TargetType.AnyPlayer && (another_potion.DynamicVars.ContainsKey("StrengthPower") || another_potion.DynamicVars.ContainsKey("DexterityPower")))//if we can't play any card, then potions that adds Strength or Dexterity are useless
			{
				//ignore
			}
			else if (another_potion.DynamicVars.ContainsKey("VulnerablePower"))//if we can't play any card, then potions that adds Vulnerability are useless
			{
				//ignore
			}
			else if (another_potion.DynamicVars.ContainsKey("Forge"))//if we can't play any card, then potions that Forges are useless
			{
				//ignore
			}
			else if ((player.Creature.Block <= 0 || (num_damage_received_from_cards == 0 && num_enemies_intends_attack == 0)) && id == "FORTIFIER")//if we have no block, or we won't receive any damage then potions that multiply block are useless
			{
				//ignore
			}
			else if (num_damage_received_from_cards == 0 && num_enemies_intends_attack == 0 && (another_potion.DynamicVars.ContainsKey("Block") || another_potion.DynamicVars.ContainsKey("PlatingPower") || another_potion.DynamicVars.ContainsKey("BufferPower") || another_potion.DynamicVars.ContainsKey("IntangiblwPower")))//if no enemy intends to attack, then potions that reduces damage taken are useless
			{
				//ignore
			}
			else if ((num_enemies_intends_attack == 0 || num_enemy_damage == 0 || num_weak_enemies == num_enemies_intends_attack) && another_potion.DynamicVars.ContainsKey("WeakPower"))//if no enemy intends to attack, then potions that weakens enemy are useless
			{
				//ignore
			}
			else if ((num_enemies_intends_attack == 0 || num_enemy_damage == 0) && another_potion.DynamicVars.ContainsKey("DamageDecrease"))//if no enemy intends to attack, then potions that weakens enemy are useless
			{
				//ignore
			}
			else if ((num_enemies_intends_attack == 0 || num_enemy_damage == 0) && (another_potion.TargetType == TargetType.AllEnemies || another_potion.TargetType == TargetType.AnyEnemy || another_potion.TargetType == TargetType.RandomEnemy) && another_potion.DynamicVars.ContainsKey("StrengthPower"))//if no enemy intends to attack, then potions that reduces enemy Strength are useless
			{
				//ignore
			}
			else if (player.Creature.CurrentHp >= player.Creature.MaxHp && (another_potion.DynamicVars.ContainsKey("MaxHp") || another_potion.DynamicVars.ContainsKey("HealPercent") || another_potion.DynamicVars.ContainsKey("RegenPower")))//if we have maximum HP, then potions that modify HPs are useless
			{
				//ignore
			}
			else if (another_potion.DynamicVars.ContainsKey("Energy") && minimum_energy_needed_to_play_card > another_potion.DynamicVars.Energy.IntValue)//if we have no cards in hand that uses energy or only cards with higher energy cost than we can get, then potions that gives us energy are useless
			{
				//ignore
			}
			else if (another_potion.DynamicVars.ContainsKey("Stars") && minimum_stars_needed_to_play_card > another_potion.DynamicVars.Stars.IntValue)//if we have no cards in hand that uses stars or only cards with higher stars cost than we can get, then potions that gives us stars are useless
			{
				//ignore
			}
			else if (handPile.Cards.Count == 0 && (id == "ASHWATER" || id == "BOTTLED_POTENTIAL" || id == "GAMBLERS_BREW"))//if we have no cards in hand, then potions that does something with cards in hand are useless
			{
				//ignore
			}
			else if (drawPile.Cards.Count == 0 && (id == "DISTILLED_CHAOS" || id == "DROPLET_OF_PRECOGNITION"))//if we have no cards in draw pile, then potions that plays cards from draw pile are useless
			{
				//ignore
			}
			else if (discardPile.Cards.Count == 0 && id == "LIQUID_MEMORIES")//if we have no cards in discard pile, then potions that plays cards from discard pile are useless
			{
				//ignore
			}
			else if (num_playable_cards == 0 && (id == "STABLE_SERUM" || id == "BLESSING_OF_THE_FORGE" || id == "TOUCH_OF_INSANITY"))//if we have no playable cards in hand, then potions that retains or improve cards in hand are useless
			{
				//ignore
			}
			else if (num_upgradable_cards == 0 && id == "BLESSING_OF_THE_FORGE")//if we have no upgradable cards in hand, then potions that upgrade cards are useless
			{
				//ignore
			}
			else if (another_potion.DynamicVars.ContainsKey("Damage"))//note: damage potions ignore vulnerable and plating
			{
				total_damage_from_potions += another_potion.DynamicVars.Damage.IntValue;
			}
			else
			{
				//NRun.Instance?.GlobalUi.TopBar.PotionContainer.AnimatePotion(another_potion);
				return;//if we get here we have some potion that can still benefit us this turn
			}
		}

		if (total_damage_from_potions > 0 && minimum_enemy_hitpoints <= total_damage_from_potions)//if we can't kill any enemy with our potions, then potions that deals damage are useless
		{
			/*
			foreach (PotionModel another_potion in player.Potions)
			{
				if (another_potion.DynamicVars.ContainsKey("Damage"))
				{
					NPotionContainer? container = NRun.Instance?.GlobalUi.TopBar.PotionContainer;
					var holders = Traverse.Create(container).Field<List<NPotionHolder>>("_holders").Value;

					var nPotionHolder = holders?.FirstOrDefault(n => n.Potion != null && n.Potion.Model == another_potion);

					var npotion = nPotionHolder?.Potion;

					Traverse.Create(npotion).Method("DoFlash").GetValue();
				}
			}
			*/
			return;
		}

		//if it falls here, we have no cards to play at all and no potions which would have sense to use, then end turn automatically
		RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.Creature.CombatState.RoundNumber));			
	}

	public static bool IsAutoPlayable(CardModel? card)
	{
		if (card?.CombatState == null) return false;

		return card.TargetType switch
		{
			TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy => true,
			TargetType.AnyEnemy => card.CombatState.HittableEnemies.Count == 1 || (ModSettings.HardSelect && ModState.TargettedEnemy != null && !ModState.TargettedEnemy.Entity.IsDead),
			_ => false
		};
	}

	public static bool IsAutoPlayable(PotionModel? potion)
	{
		if (potion?.Owner.Creature.CombatState == null) return false;

		return potion.TargetType switch
		{
			TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy => true,
			TargetType.AnyEnemy => potion.Owner.Creature.CombatState.HittableEnemies.Count == 1 || (ModSettings.HardSelect && ModState.TargettedEnemy != null && !ModState.TargettedEnemy.Entity.IsDead),
			_ => false
		};
	}

	public static bool CardsEqual(CardModel? cardA, CardModel? cardB)
	{
		if (cardA == null || cardB == null) return false;
		if (cardA.Type != cardB.Type) return false;
		else if (cardA.Type != cardB.Type) return false;
		else if (cardA.Rarity != cardB.Rarity) return false;
		else if (cardA.Id.Entry != cardB.Id.Entry) return false;
		else if (cardA.CurrentUpgradeLevel != cardB.CurrentUpgradeLevel) return false;
		if (cardA.Enchantment == null && cardB.Enchantment != null) return false;
		else if (cardA.Enchantment != null && cardB.Enchantment != null)
		{
			if (cardA.Enchantment.Id.Entry != cardB.Enchantment.Id.Entry)
			{
				return false;
			}
		}
		if (cardA.Affliction == null && cardB.Affliction != null) return false;
		else if (cardA.Affliction != null && cardB.Affliction != null)
		{
			if (cardA.Affliction.Id.Entry != cardB.Affliction.Id.Entry)
			{
				return false;
			}
		}
		return true;
	}

	public static class ModState
	{
		public static Player? CurrentPlayer;
		public static NCreature? TargettedEnemy;
		public static bool DoNotHideReticle;
		public static ulong IgnoreEnemyClickUntilMs;
	}
}
