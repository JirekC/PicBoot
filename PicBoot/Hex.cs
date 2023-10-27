using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace PicBoot
{
    class MemBlock
    {
        public uint first_addr;
        public byte[] data;
    }

    class Hex
    {
        protected BlockingCollection<string> log_queue = null;
        public List<MemBlock> blocks = new List<MemBlock>();

        public Hex(BlockingCollection<string> _log_queue)
        {
            log_queue = _log_queue;
        }

        /*
         * address: only low 16 bits are used
         * length must be 0 .. 255
         */
        protected string CreateSingleLine(uint address, byte rec_type, byte[] data, uint offset, uint length)
        {
            address &= 0xFFFF;
            // write line-head
            string str = $":{length:X2}{address:X4}{rec_type:X2}";
            byte chksum = (byte)(length + (byte)address + (byte)(address >> 8) + rec_type);
            // write data
            for(uint i = 0; i < length; i++)
            {
                str += $"{data[offset + i]:X2}";
                chksum += data[offset + i];
            }
            // write checksum
            str += $"{(byte)(-1 * (int)chksum):X2}";

            return str;
        }

        /*
         * Generates 0x04 type record. Uses upper 16 bits of address
         */
        protected string NewAddressBlock(uint address)
        {
            byte[] line_data = new byte[2];
            line_data[0] = (byte)(address >> 24);
            line_data[1] = (byte)(address >> 16);
            return CreateSingleLine(0, 0x04, line_data, 0, 2);
        }

        /*
         * uses I32HEX format
         */
        public void SaveToFile(string file, uint bytes_per_addr)
        {
            StreamWriter writer = new StreamWriter(file);
            uint addrs_per_line = 16 / bytes_per_addr;
            byte[] line_data = new byte[addrs_per_line * bytes_per_addr];

            foreach (MemBlock mb in blocks)
            {
                uint mbidx = 0; 
                uint addr_cntr = mb.first_addr;

                writer.WriteLine(NewAddressBlock(addr_cntr)); // upper 16 bits of block's address
                while (mbidx < mb.data.Length)
                {
                    uint line_len = 0; // in words (bytes_per_addr) units
                    uint start_idx = mbidx;
                    for (uint i = 0; i < addrs_per_line; i++)
                    {
                        Array.Copy(mb.data, mbidx, line_data, i * bytes_per_addr, bytes_per_addr);
                        mbidx += bytes_per_addr;
                        line_len++;
                        if (mbidx >= mb.data.Length)
                            break; // end of block reached
                        if ((addr_cntr & 0xFFFF0000) != ((addr_cntr + line_len) & 0xFFFF0000))
                        {
                            break; // 64 kB page boundary reached
                        }
                    }
                    writer.WriteLine(CreateSingleLine(addr_cntr, 0x00, mb.data, start_idx, line_len * bytes_per_addr));
                    if ((addr_cntr & 0xFFFF0000) != ((addr_cntr + line_len) & 0xFFFF0000))
                    {
                        // 64 kB page boundary reached
                        writer.WriteLine(NewAddressBlock(addr_cntr + line_len));
                    }
                    addr_cntr += line_len;
                }
            }
            writer.WriteLine(":00000001FF"); // EOF
            writer.Close();
        }

        protected int GetBlockIdxByAddr(uint addr, uint bytes_per_addr)
        {
            int idx = 0;
            foreach(var mb in blocks)
            {
                if(addr >= mb.first_addr && addr < (mb.first_addr + (mb.data.Length / bytes_per_addr)))
                {
                    return idx;
                }
                idx++;
            }
            throw new Exception($"Address 0x{addr:X} not found in memory regions.");
        }

        /*
         * Blocks must be properly allocated before calling this.
         * Single line can contain data only from single memory-block.
         * Uses I32HEX format.
         */
        public bool LoadFromFile(string file, uint bytes_per_addr)
        {
            bool ret_val = true;
            StreamReader reader = new StreamReader(file);
            string line;
            uint line_cntr = 0;
            uint addr_cntr = 0;

            // init to 0xFF
            foreach(var mb in blocks)
            {
                Parallel.For(0, mb.data.Length, index => mb.data[index] = 0xFF);
            }
            // load from file, line-by-line
            while ((line = reader.ReadLine()) != null)
            {
                line_cntr++;
                line = line.Trim(); // remove whitespaces from begin and end of line
                if (line.Length > 0)
                {
                    if (line[0] != ':')
                    {
                        continue; // non IHEX line
                    }
                }
                else
                {
                    continue; // empty line
                }
                if (line.Length < 11)
                {
                    log_queue?.TryAdd($"Ignoring line [{line_cntr}]: {line}\r\n");
                    ret_val = false;
                    continue;
                }
                // valid IHEX line, now check data-validity
                try
                {
                    byte byte_cnt = Convert.ToByte(line.Substring(1, 2), 16);
                    byte chksum = byte_cnt;
                    if(line.Length != 2 * (uint)byte_cnt + 11)
                    {
                        log_queue?.TryAdd($"ERROR: Invalid data-field length @line [{line_cntr}]: {line}\r\n");
                        ret_val = false;
                        continue; // invalid line-length
                    }
                    ushort addr = Convert.ToUInt16(line.Substring(3, 4), 16);
                    chksum += (byte)(addr + (addr >> 8));
                    byte rec_type = Convert.ToByte(line.Substring(7, 2), 16);
                    chksum += rec_type;
                    byte[] data = new byte[byte_cnt];
                    for(uint i = 0; i < (uint)byte_cnt; i++)
                    {
                        data[i] = Convert.ToByte(line.Substring(9 + 2 * (int)i, 2), 16);
                        chksum += data[i];
                    }
                    chksum += Convert.ToByte(line.Substring(line.Length - 2, 2), 16);
                    if(chksum != 0)
                    {
                        log_queue?.TryAdd($"ERROR: Invalid checksum @line [{line_cntr}]: {line}\r\n");
                        ret_val = false;
                    }
                    // parsed, now store
                    switch(rec_type)
                    {
                        case 0x00: // data
                            addr_cntr = (addr_cntr & 0xFFFF0000) | (uint)addr;
                            int bidx = GetBlockIdxByAddr(addr_cntr, bytes_per_addr); // expects that sinle line contains data from single memory-block
                            Array.Copy(data, 0, blocks[bidx].data, (addr_cntr - blocks[bidx].first_addr) * bytes_per_addr, byte_cnt);
                            addr_cntr += byte_cnt / bytes_per_addr; // if there is address overflow (over 16 bits) at single line
                            break;
                        case 0x01: // EOF
                            reader.Close();
                            return ret_val;
                        case 0x04: // Extended Linear Address - set upper 16 bits of address counter
                            addr_cntr = (uint)data[0] << 24 | (uint)data[1] << 16;
                            break;
                        default:
                            log_queue?.TryAdd($"ERROR: Unknown record type @line [{line_cntr}]: {line}\r\n");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    log_queue?.TryAdd($"ERROR: {ex.Message}\r\n");
                    ret_val = false;
                }
            }
            reader.Close();

            return ret_val;
        }

    }
}
