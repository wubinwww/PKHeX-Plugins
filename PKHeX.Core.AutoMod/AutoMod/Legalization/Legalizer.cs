using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PKHeX.Core.AutoMod
{
    /// <summary>
    /// Contains logic to create a <see cref="PKM"/> from its elemental details.
    /// </summary>
    public static class Legalizer
    {
        public static bool EnableEasterEggs { get; set; } = true;

        /// <summary>
        /// Tries to regenerate the <see cref="pk"/> into a valid pkm.
        /// </summary>
        /// <param name="pk">Currently invalid pkm data</param>
        /// <returns>Legalized PKM (hopefully legal)</returns>
        public static PKM Legalize(this PKM pk)
        {
            var tr = TrainerSettings.GetSavedTrainerData(pk.Format);
            return tr.MutateLanguage((LanguageID)pk.Language).Legalize(pk);
        }

        /// <summary>
        /// Tries to regenerate the <see cref="pk"/> into a valid pkm.
        /// </summary>
        /// <param name="tr">Source/Destination trainer</param>
        /// <param name="pk">Currently invalid pkm data</param>
        /// <returns>Legalized PKM (hopefully legal)</returns>
        public static PKM Legalize(this ITrainerInfo tr, PKM pk)
        {
            var set = new RegenTemplate(pk, tr.Generation);
            return tr.GetLegalFromTemplateTimeout(pk, set, out _);
        }

        /// <summary>
        /// Imports <see cref="sets"/> to a provided <see cref="arr"/>, with a context of <see cref="tr"/>.
        /// </summary>
        /// <param name="tr">Source/Destination trainer</param>
        /// <param name="sets">Set data to import</param>
        /// <param name="arr">Current list of data to write to</param>
        /// <param name="invalidAPISets">Returned list of invalid sets that failed to be generated by this api call</param>
        /// <param name="timedoutSets">Returned list of sets that failed to be generated in the premitted time.</param>
        /// <param name="start">Starting offset to place converted details</param>
        /// <param name="overwrite">Overwrite</param>
        /// <returns>Result code indicating success or failure</returns>
        public static AutoModErrorCode ImportToExisting(this SaveFile tr, IReadOnlyList<ShowdownSet> sets, IList<PKM> arr, out List<RegenTemplate> invalidAPISets, out List<RegenTemplate> timedoutSets, int start = 0, bool overwrite = true)
        {
            var emptySlots = overwrite
                ? Enumerable.Range(start, sets.Count).Where(set => set < arr.Count).ToList()
                : FindAllEmptySlots(arr, start);
            invalidAPISets = new List<RegenTemplate>();
            timedoutSets = new List<RegenTemplate>();

            if (emptySlots.Count < sets.Count)
                return AutoModErrorCode.NotEnoughSpace;

            var generated = 0;
            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                var regen = new RegenTemplate(set, tr.Generation);
                if (set.InvalidLines.Count > 0)
                    return AutoModErrorCode.InvalidLines;

                Debug.WriteLine($"Generating Set: {GameInfo.Strings.Species[set.Species]}");
                var pk = tr.GetLegalFromSet(regen, out var msg);
                pk.ResetPartyStats();
                pk.SetBoxForm();
                if (msg == LegalizationResult.Failed)
                    invalidAPISets.Add(regen);
                if (msg == LegalizationResult.Timeout)
                    timedoutSets.Add(regen);

                var index = emptySlots[i];
                tr.SetBoxSlotAtIndex(pk, index);
                generated++;
            }

            foreach (var r in timedoutSets)
                Dump(r);
            foreach (var r in invalidAPISets)
                Dump(r, true);
            Debug.WriteLine($"API Generated Sets: {generated - invalidAPISets.Count - timedoutSets.Count}/{sets.Count}, {invalidAPISets.Count} were invalid and {timedoutSets.Count} timed out.");
            foreach (var set in invalidAPISets)
                Debug.WriteLine(set.Text);
            return AutoModErrorCode.None;
        }

        public static void Dump(RegenTemplate set, bool invalid = false)
        {
            var msg = (invalid
                          ? $"[Invalid] [DateTime: {DateTime.Now}]"
                          : $"[Timeout : {APILegality.Timeout} seconds] [DateTime: {DateTime.Now}]") +
                      Environment.NewLine + set.Text + Environment.NewLine;
            System.IO.File.AppendAllText("error_log.txt", msg);
        }

        /// <summary>
        /// Imports a <see cref="set"/> to create a new <see cref="PKM"/> with a context of <see cref="tr"/>.
        /// </summary>
        /// <param name="tr">Source/Destination trainer</param>
        /// <param name="set">Set data to import</param>
        /// <param name="msg">Result code indicating success or failure</param>
        /// <returns>Legalized PKM (hopefully legal)</returns>
        public static PKM GetLegalFromSet(this ITrainerInfo tr, IBattleTemplate set, out LegalizationResult msg)
        {
            var template = PKMConverter.GetBlank(tr.Generation, (GameVersion)tr.Game);
            template.ApplySetDetails(set);
            return tr.GetLegalFromSet(set, template, out msg);
        }

        /// <summary>
        /// Regenerates the set by searching for an encounter that can generate the template.
        /// </summary>
        /// <param name="tr">Trainer Data that was passed in</param>
        /// <param name="set">Showdown set being used</param>
        /// <param name="template">template PKM to legalize</param>
        /// <param name="msg">Legalization result</param>
        /// <returns>Legalized pkm</returns>
        private static PKM GetLegalFromSet(this ITrainerInfo tr, IBattleTemplate set, PKM template, out LegalizationResult msg)
        {
            if (set is ShowdownSet s)
                set = new RegenTemplate(s, tr.Generation);

            msg = tr.TryAPIConvert(set, template, out PKM pk);
            if (msg == LegalizationResult.Regenerated)
                return pk;

            if (EnableEasterEggs)
            {
                var gen = EasterEggs.GetGeneration(template.Species);
                var species = (int)EasterEggs.GetMemeSpecies(gen);
                template.Species = species;
                var legalencs = tr.GetRandomEncounter(species, null, set.Shiny, out var legal);
                if (legalencs && legal != null)
                    template = legal;
                template.SetNickname(EasterEggs.GetMemeNickname(gen));
            }
            return template;
        }

        /// <summary>
        /// API Legality
        /// </summary>
        /// <param name="tr">trainer data</param>
        /// <param name="set">showdown set to legalize from</param>
        /// <param name="template">pkm file to legalize</param>
        /// <param name="pkm">legalized pkm file</param>
        /// <returns>bool if the pokemon was legalized</returns>
        public static LegalizationResult TryAPIConvert(this ITrainerInfo tr, IBattleTemplate set, PKM template, out PKM pkm)
        {
            pkm = tr.GetLegalFromTemplateTimeout(template, set, out LegalizationResult satisfied);
            if (satisfied != LegalizationResult.Regenerated)
                return satisfied;

            var trainer = TrainerSettings.GetSavedTrainerData(pkm, tr);
            pkm.SetAllTrainerData(trainer);
            return LegalizationResult.Regenerated;
        }

        /// <summary>
        /// Method to find all empty slots in a current box
        /// </summary>
        /// <param name="data">Box Data of the save file</param>
        /// <param name="start">Starting position for finding an empty slot</param>
        /// <returns>A list of all indices in the current box that are empty</returns>
        private static List<int> FindAllEmptySlots(IList<PKM> data, int start)
        {
            var emptySlots = new List<int>();
            for (int i = start; i < data.Count; i++)
            {
                if (data[i].Species < 1)
                    emptySlots.Add(i);
            }
            return emptySlots;
        }
    }
}