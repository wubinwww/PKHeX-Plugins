﻿using System;
using System.Diagnostics;
using System.Reflection;

namespace PKHeX.Core.Injection
{
    public class LiveHeXController
    {
        private readonly ISaveFileProvider SAV;
        public readonly IPKMView Editor;
        public PokeSysBotMini Bot;

        public LiveHeXController(ISaveFileProvider boxes, IPKMView editor, InjectorCommunicationType ict)
        {
            SAV = boxes;
            Editor = editor;
            var ValidVers = RamOffsets.GetValidVersions(boxes.SAV);
            Bot = new PokeSysBotMini(ValidVers[0], ict);
        }

        public void ChangeBox(int box)
        {
            if (!Bot.Connected)
                return;

            var sav = SAV.SAV;
            if ((uint)box >= sav.BoxCount)
                return;

            ReadBox(box);
        }

        public void ReadBox(int box)
        {
            var sav = SAV.SAV;
            var len = SAV.SAV.BoxSlotCount * (RamOffsets.GetSlotSize(Bot.Version) + RamOffsets.GetGapSize(Bot.Version));
            var data = Bot.ReadBox(box, len);
            sav.SetBoxBinary(data, box);
            SAV.ReloadSlots();
        }

        public void WriteBox(int box)
        {
            var boxData = SAV.SAV.GetBoxBinary(box);
            Bot.SendBox(boxData, box);
        }

        public void WriteActiveSlot(int box, int slot)
        {
            var pkm = Editor.PreparePKM();
            pkm.ResetPartyStats();
            var data = RamOffsets.WriteBoxData(Bot.Version) ? pkm.EncryptedBoxData : pkm.EncryptedPartyData;
            Bot.SendSlot(data, box, slot);
        }

        public void ReadActiveSlot(int box, int slot)
        {
            var data = Bot.ReadSlot(box, slot);
            var pkm = SAV.SAV.GetDecryptedPKM(data);
            Editor.PopulateFields(pkm);
        }

        public bool ReadOffset(ulong offset, RWMethod method = RWMethod.Heap)
        {
            byte[] data;
            if (Bot.com is not ICommunicatorNX nx) data = Bot.ReadOffset(offset);
            else data = method switch
            {
                RWMethod.Heap => Bot.ReadOffset(offset),
                RWMethod.Main => nx.ReadBytesMain(offset, Bot.SlotSize),
                RWMethod.Absolute => nx.ReadBytesAbsolute(offset, Bot.SlotSize),
                _ => Bot.ReadOffset(offset)
            };
            var pkm = SAV.SAV.GetDecryptedPKM(data);

            // Since data might not actually exist at the user-specified offset, double check that the pkm data is valid.
            if (!pkm.ChecksumValid)
                return false;

            Editor.PopulateFields(pkm);
            return true;
        }

        public byte[] ReadRAM(ulong offset, int size) => Bot.com.ReadBytes(offset, size);

        public void WriteRAM(ulong offset, byte[] data) => Bot.com.WriteBytes(data, offset);
    }
}