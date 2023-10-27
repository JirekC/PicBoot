using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;


namespace PicBoot
{
    public class Bootloader
    {
        const int retry_count = 3; // max 3 retries of communication before error

        const byte STX = 0x0F;
        const byte ETX = 0x04;
        const byte DLE = 0x05;

        public enum BootCmd : byte
        {
            RD_VER  = 0x00,
            RD_PROG = 0x01,
            WR_PROG = 0x02,
            ER_PROG = 0x03,
            RD_DATA = 0x04,
            WR_DATA = 0x05,

            RESET = 0xFF,
        }

        //protected object port_mutex = new object();
        protected SerialPort sp = new SerialPort();
        protected BlockingCollection<string> log_queue = null;
        protected byte[] last_resp = new byte[256]; // not protected by mutex - read it only when cmd finishes (status == 0)
        protected int last_resp_length = 0; // number of bytes received during last ReceivePacket() call

        /* Protected by mutex --> */
        protected object data_mutex = new object();

        protected bool _stop_work = false; 
        public bool stop_work // set to true by another threads to signal stop request (used by DoCmd())
        {
            get
            {
                bool i;
                lock (data_mutex)
                {
                    i = _stop_work;
                }
                return i;
            }
            set
            {
                lock (data_mutex)
                {
                    _stop_work = value;
                }
            }
        }

        protected int _status = -1;
        public int status // (0) = iddle; ( <0) = error and/or port closed; ( >0) = cmd in progress
        {
            get
            {
                int i;
                lock (data_mutex)
                {
                    i = _status;
                }
                return i;
            }
            set
            {
                lock(data_mutex)
                {
                    _status = value;
                }
            }
        }

        protected string _last_exception = string.Empty;
        public string last_exception // last exception explanation
        {
            get
            {
                string s;
                lock (data_mutex)
                {
                    s = _last_exception;
                    _last_exception = string.Empty;
                }
                return s;
            }
            set
            {
                lock(data_mutex)
                {
                    _last_exception = value;
                }
            }
        }
        /* <-- */

        public Bootloader(BlockingCollection<string> _log_queue)
        {
            log_queue = _log_queue;
        }

        public bool Open(string port, int baud, int timeout)
        {
            bool ret_val = true;

            //lock (port_mutex)
            {
                try
                {
                    if (sp.IsOpen)
                    {
                        ret_val = false;
                        last_exception = "Port already opened.";
                    }
                    else
                    {
                        sp.PortName = port;
                        sp.BaudRate = baud;
                        sp.ReadTimeout = timeout;
                        sp.Open();
                        status = 0;
                    }
                }
                catch (Exception ex)
                {
                    ret_val = false;
                    status = -1;
                    last_exception = ex.Message; // store reason of fail
                }
            }
            return ret_val;
        }

        public bool TryClose()
        {
            bool retval = false;
            //lock (port_mutex)
            {
                if (sp.IsOpen && status <= 0)
                {
                    // opened and nothing in progress
                    sp.Close();
                }
                if (sp.IsOpen == false)
                {
                    status = -1; // closed port
                    retval = true;
                }
            }
            return retval;
        }

        public bool IsOpened()
        {
            bool ret_val;
            //lock(port_mutex) // this blocks MainForm.timer1_Tick, commented out - SerialPort has to be threadsafe
            {
                ret_val = sp.IsOpen;
            }
            return ret_val;
        }

        /* packet format:
	    ; <STX><STX><PAYLOAD><CHKSUM><ETX>
	    ;  ________/         \_____________________
	    ; /                                        \
	    ; <COMMAND><DLEN><ADDRL><ADDRH><ADDRU><DATA>...
	    ;
	    ; Definitions:
	    ;
	    ; STX - Start of packet indicator
	    ; COMMAND - Base command
	    ; DLEN - Length of data associated to the command [in rd/wr/er blocks]
	    ; ADDR - Address up to 24 bits, must be rd/wr/er-block alligned
	    ; DATA - Data (if any)
	    ; CHKSUM - The 8-bit two's compliment sum of PAYLOAD
	    ; ETX - End of packet indicator
	    */
        protected void SendPacket(byte[] data)
        {
            byte[] buf = new byte[2 * data.Length + 5]; // two times if all characters will need DLE prefix
            byte chksum = 0, ch;
            int idx, jdx;

            buf[0] = STX;
            buf[1] = STX;
            idx = 2;
            for(jdx = 0; jdx < data.Length; jdx++)
            {
                ch = data[jdx];
                if((ch == STX) || (ch == ETX) || (ch == DLE))
                {
                    buf[idx++] = DLE; // use escape prefix, if data contains special characters
                }
                buf[idx++] = ch;
                chksum += ch;
            }
            ch = (byte)(-1 * chksum);
            if ((ch == STX) || (ch == ETX) || (ch == DLE))
            {
                buf[idx++] = DLE; // use escape prefix, if checksum contains special character
            }
            buf[idx++] = ch;
            buf[idx++] = ETX;
            //lock (port_mutex)
            {
                // flush receiver before new cmd
                sp.ReadExisting();
                // send it
                sp.Write(buf, 0, idx);
            }
        }

        protected bool ReceivePacket()
        {
            bool fail = false;
            int state = 0;
            int cntr = 0;
            byte chksum = 0;

            try
            {
                while (true)
                {
                    int ch_i;
                    //lock (port_mutex) // blokuje GUI během timeoutu: Timer volá IsOpened()
                    {
                        ch_i = sp.ReadByte();
                    }
                    if (ch_i < 0)
                    {
                        //fail = true;
                        //break;
                    }
                    else
                    {
                        byte ch = (byte)ch_i;
                        switch (state)
                        {
                            case 0: // waiting for first STX
                                if (ch == STX)
                                {
                                    state = 1;
                                }
                                break;
                            case 1: // waiting for second STX
                                if (ch == STX)
                                {
                                    cntr = 0; // reset counter & checksum
                                    chksum = 0;
                                    state = 2;
                                }
                                else
                                {
                                    state = 0; // back to begin
                                }
                                break;
                            case 2: // payload rx
                                if (ch == STX)
                                {
                                    state = 1; // next start
                                }
                                else if (ch == ETX)
                                {
                                    if (chksum == 0)
                                    {
                                        // OK, valid msg received
                                        last_resp_length = cntr;
                                        goto FINITO;
                                    }
                                    else
                                    {
                                        last_resp_length = 0;
                                        fail = true;
                                        goto FINITO;
                                    }
                                }
                                else if (ch == DLE)
                                {
                                    state = 3; // store next character regardless of value
                                }
                                else
                                {
                                    last_resp[cntr++] = ch;
                                    chksum += ch;
                                }
                                break;
                            case 3: // next char after DLE - do not check value
                                last_resp[cntr++] = ch;
                                chksum += ch;
                                state = 2;
                                break;
                        }
                        if (cntr >= last_resp.Length)
                        {
                            // double the size of response buffer
                            Array.Resize<byte>(ref last_resp, 2 * last_resp.Length);
                        }
                    }
                }
            }
            catch
            {
                fail = true;
            }
            FINITO:

            return !fail;
        }

        /**
         * \param len   length of message in rd/wr/er blocks (not bytes!)
         * returns result in last_resp
         */
        protected void DoCmd(BootCmd cmd, uint addr, byte len, byte[] data, uint offset, uint bytes)
        {
            byte[] req;

            // build the message
            if (data != null)
            {
                req = new byte[bytes + 5]; // cmd, len, 3*addr, data bytes
                Array.Copy(data, offset, req, 5, bytes);
            }
            else
            {
                req = new byte[5];
            }
            // header
            req[0] = (byte)cmd;
            req[1] = len;
            req[2] = (byte)addr;
            req[3] = (byte)(addr >> 8);
            req[4] = (byte)(addr >> 16);

            try
            {
                for (int i = 0; i < retry_count; i++)
                {
                    if(stop_work)
                    {
                        stop_work = false;
                        throw new Exception("Aborted by user.");
                    }
                    SendPacket(req); // can throw exception, eg.: port closed
                    if (ReceivePacket())
                    {
                        status = 0; // iddle
                        return; // everything OK
                    }
                }
                throw new Exception("Target did not respond correctly.");
            }
            catch (Exception ex)
            {
                last_exception = ex.Message;
                status = -1; // err found
                //lock (port_mutex)
                {
                    sp.Close();
                }
            }
        }

        public bool EraseProgRegion(CPU_Params cp, AddrRange region)
        {
            bool ret_val = true;

            if (region.first > region.last)
            {
                last_exception = $"Bad program memory region: 0x{region.first:X} .. 0x{region.last:X}";
                log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                return false; // bad param
            }
            
            stop_work = false;
            uint no_pages = (region.last - region.first + 1) / cp.erase_block;
            uint addr = region.first; // in bytes
            while (no_pages > 0)
            {
                byte chunk_len; // in pages
                if(no_pages > 0xFF)
                {
                    chunk_len = 0xFF;
                    no_pages -= 0xFF;
                }
                else
                {
                    chunk_len = (byte)no_pages;
                    no_pages = 0;
                }
                status = 1; // Working hard ...
                log_queue?.TryAdd($"Erasing from: 0x{addr:X}\r\n");
                DoCmd(BootCmd.ER_PROG, addr, chunk_len, null, 0, 0);
                addr += chunk_len * cp.erase_block;
                if(status != 0)
                {
                    // error happend
                    log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                    ret_val = false;
                    break;
                }
            }

            return ret_val;
        }

        public byte[] ReadProgRegion(CPU_Params cp, AddrRange region)
        {
            if (region.first > region.last)
            {
                last_exception = $"Bad program memory region: 0x{region.first:X} .. 0x{region.last:X}";
                log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                return null; // bad param
            }
            
            stop_work = false;
            uint no_bytes = (region.last - region.first + 1) * cp.bytes_per_addr;
            byte[] data = new byte[no_bytes];
            uint no_blocks = (region.last - region.first + 1) / cp.read_block;
            uint max_blocks_per_once =
                cp.max_pkt_size / (cp.read_block * cp.bytes_per_addr);
            if (max_blocks_per_once > 0xFF)
                max_blocks_per_once = 0xFF;
            uint addr = region.first;
            uint didx = 0; // index in data[]

            while(no_blocks > 0)
            {
                byte chunk_len; // in blocks
                if (no_blocks > max_blocks_per_once)
                {
                    chunk_len = (byte)max_blocks_per_once; // limited to 0xFF above
                    no_blocks -= max_blocks_per_once;
                }
                else
                {
                    chunk_len = (byte)no_blocks;
                    no_blocks = 0;
                }
                uint bytes_to_read = chunk_len * cp.read_block * cp.bytes_per_addr;
                status = 1; // Working hard ...
                log_queue?.TryAdd($"Reading from: 0x{addr:X}\r\n");
                DoCmd(BootCmd.RD_PROG, addr, chunk_len, null, 0, 0);
                if (status != 0)
                {
                    // error happend
                    log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                    data = null; // return null when error happend
                    break;
                }
                else if ((last_resp_length - 6) != bytes_to_read)
                {
                    status = -1;
                    last_exception = "Invalid length of response.";
                    log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                    data = null; // return null when error happend
                    break;
                }
                Array.Copy(last_resp, 5, data, didx, bytes_to_read);
                didx += bytes_to_read;
                addr += chunk_len * cp.read_block;
            }

            return data;
        }

        public bool WriteProgRegion(CPU_Params cp, AddrRange region, in byte[] data)
        {
            bool ret_val = true;

            if (region.first > region.last)
            {
                last_exception = $"Bad program memory region: 0x{region.first:X} .. 0x{region.last:X}";
                log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                return false; // bad param
            }

            stop_work = false;
            uint no_bytes = (region.last - region.first + 1) * cp.bytes_per_addr;
            if (data.Length < no_bytes)
                return false; // low amount of data ;)
            uint no_blocks = (region.last - region.first + 1) / cp.write_block; // integer division: only full blocks will be written
            uint max_blocks_per_once =
                cp.max_pkt_size / (cp.write_block * cp.bytes_per_addr);
            if (max_blocks_per_once > 0xFF)
                max_blocks_per_once = 0xFF;
            uint addr = region.first;
            uint didx = 0; // index in data[]

            while (no_blocks > 0)
            {
                byte chunk_len; // in blocks
                if (no_blocks > max_blocks_per_once)
                {
                    chunk_len = (byte)max_blocks_per_once; // limited to 0xFF above
                    no_blocks -= max_blocks_per_once;
                }
                else
                {
                    chunk_len = (byte)no_blocks;
                    no_blocks = 0;
                }
                uint bytes_to_write = chunk_len * cp.write_block * cp.bytes_per_addr;
                status = 1; // Working hard ...
                log_queue?.TryAdd($"Writing to: 0x{addr:X}\r\n");
                DoCmd(BootCmd.WR_PROG, addr, chunk_len, data, didx, bytes_to_write);
                if (status != 0)
                {
                    // error happend
                    log_queue?.TryAdd($"ERROR: {last_exception}\r\n");
                    ret_val = false;
                    break;
                }
                didx += bytes_to_write;
                addr += chunk_len * cp.write_block;
            }

            return ret_val;
        }

        /* start app by writing "non 0xFF" value to last position of data memory */
        public bool StartApp(CPU_Params cp)
        {
            bool ret_val = true;

            stop_work = false;
            if(!IsOpened())
            {
                log_queue?.TryAdd("ERROR: Port is closed.\r\n");
                status = -1;
            }
            else
            {
                status = 1; // Working hard ...
                log_queue?.TryAdd("Starting application code...\r\n");
                DoCmd(BootCmd.WR_DATA, cp.data_range.last, 1, new byte[] { 0 }, 0, 1);
                DoCmd(BootCmd.RESET, 0, 0, null, 0, 0);
            }

            return ret_val;
        }

    }
}
