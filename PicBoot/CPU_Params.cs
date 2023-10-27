using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PicBoot
{
    public class AddrRange
    {
        public uint first;
        public uint last;
    }

    public class CPU_Params
    {
        public string name;
        public int baud;                 // recomended baud-rate (300 .. 1Meg is valid range)
        public int timeout;              // UART read timeout

        public uint write_block;         // write to program memory in integer multiplies of this [Words]
        public uint read_block;          // read from program memory in integer multiplies of this [Words]
        public uint erase_block;         // program memory erase-page size [Words]
        public uint max_pkt_size;        // maximal packet size [Bytes] Payload only excluding sync and checksum
        public uint bytes_per_addr;      // program memory WORD size
        public List<AddrRange> prog_range;  // program memory (FLASH) address regions
        public AddrRange data_range;        // data EEPROM address range
    }
}
