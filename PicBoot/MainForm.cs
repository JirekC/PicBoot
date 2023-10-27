using System;
using System.Drawing;
using System.IO.Ports;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace PicBoot
{
    public partial class MainForm : Form
    {
        Bootloader bl;
        CPU_Params cp;
        BlockingCollection<string> log_queue = new BlockingCollection<string>(64);

        public MainForm(CPU_Params _cp)
        {
            cp = _cp;
            bl = new Bootloader(log_queue);
            InitializeComponent();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // ** most important: color theme :)
            this.BackColor = Color.Black;
            this.ForeColor = Color.White;
            //Now for every control that does need an extra color / property to be set:
            foreach (Control ctrl in this.Controls)
            {
                ctrl.BackColor = this.BackColor;
                ctrl.ForeColor = this.ForeColor;
                //Maybe do more here...
            }
            this.Invalidate(); //Forces a re-draw of your controls / form

            Text = cp.name; // rename window caption

            // ** find all serial ports in the system
            cbPort.Items.AddRange(SerialPort.GetPortNames());
            if(cbPort.Items.Count < 1)
            {
                MessageBox.Show("No serial port found!", "ERROR");
                this.Close();
                return;
            }
            cbPort.SelectedIndex = 0;

            // ** recommended baudrate
            tbSpeed.Text = cp.baud.ToString();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            while (!bl.TryClose()) { }
        }

        private void SetBtnsEnDis(bool enabled)
        {
            btnConnect.Enabled = enabled;
            btnErase.Enabled = enabled;
            btnRead.Enabled = enabled;
            btnWrite.Enabled = enabled;
            btnRun.Enabled = enabled;
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            if(!bl.IsOpened())
            {
                // serial port is not opened yet
                int baud;
                if(!int.TryParse(tbSpeed.Text, out baud))
                {
                    baud = 0;
                }
                if(baud < 300 || baud > 1000000)
                {
                    tbLogs.AppendText("ERROR: Speed must be a number <300 .. 1000000>\r\n");
                    return;
                }
                if(!bl.Open(cbPort.SelectedItem.ToString(), baud, cp.timeout))
                {
                    tbLogs.AppendText($"ERROR: Can't open selected serial port: {bl.last_exception}");
                    return;
                }
                //bl.GetVersion(sp);
                cbPort.Enabled = false;
                tbSpeed.Enabled = false;
            }
            else
            {
                // serial port already opened
                while (!bl.TryClose()) { };
            }
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            // connect button
            if (bl.IsOpened())
            {
                btnConnect.Text = "Disconnect";
            }
            else
            {
                cbPort.Enabled = true;
                tbSpeed.Enabled = true;
                btnConnect.Text = "Connect";
            }
            // logs
            while (log_queue.TryTake(out string msg))
            {
                tbLogs.AppendText(msg);
            }
        }

        private async void btnErase_Click(object sender, EventArgs e)
        {
            SetBtnsEnDis(false);
            foreach (var r in cp.prog_range)
            {
                await Task.Run(() => bl.EraseProgRegion(cp, r));
            }
            log_queue.TryAdd("Command finished.\r\n");
            SetBtnsEnDis(true);
        }

        private async void btnRead_Click(object sender, EventArgs e)
        {
            Hex prog_img = new Hex(log_queue);

            SetBtnsEnDis(false);
            // download from CPU
            foreach (var r in cp.prog_range)
            {
                MemBlock mb = new MemBlock
                {
                    first_addr = r.first,
                    data = await Task.Run(() => bl.ReadProgRegion(cp, r))
                };
                if (mb.data == null)
                {
                    goto ExceptionExit; // error happend
                }
                prog_img.blocks.Add(mb);
            }
            // save to .hex file
            if(saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                prog_img.SaveToFile(saveFileDialog.FileName, cp.bytes_per_addr);
            }
            ExceptionExit:
            log_queue.TryAdd("Command finished.\r\n");
            SetBtnsEnDis(true);
        }

        private async void btnWrite_Click(object sender, EventArgs e)
        {
            Hex prog_img = new Hex(log_queue);

            SetBtnsEnDis(false);
            // create memory blocks
            foreach (var r in cp.prog_range)
            {
                if(r.first > r.last)
                {
                    log_queue.TryAdd($"ERROR: Bad program memory region: 0x{r.first:X} .. 0x{r.last:X}\r\n");
                    continue;
                }
                prog_img.blocks.Add(new MemBlock()
                {
                    first_addr = r.first,
                    data = new byte[(r.last - r.first + 1) * cp.bytes_per_addr]
                });
            }
            // load from .hex file
            if (openFileDialog.ShowDialog() != DialogResult.OK)
            {
                goto ExceptionExit; // user aborted
            }
            if(!prog_img.LoadFromFile(openFileDialog.FileName, cp.bytes_per_addr))
            {
                // something bad happend inside
                if(MessageBox.Show("Some errors found during loading hex file (see log). Do You want to continue?",
                    "ERROR",
                    MessageBoxButtons.YesNo) != DialogResult.Yes)
                {
                    goto ExceptionExit; // user decided to abort
                }
            }
            // write into CPU
            foreach(var mb in prog_img.blocks)
            {
                await Task.Run(() => bl.WriteProgRegion(
                    cp,
                    new AddrRange()
                    {
                        first = mb.first_addr,
                        last = mb.first_addr + ((uint)mb.data.Length / cp.bytes_per_addr) - 1
                    },
                    mb.data
                ));
            }
            ExceptionExit:
            log_queue.TryAdd("Command finished.\r\n");
            SetBtnsEnDis(true);
        }

        private async void btnRun_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Hey Sir, are You realy sure?",
                    "Interesting question",
                    MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                SetBtnsEnDis(false);
                await Task.Run(() => bl.StartApp(cp));
                log_queue.TryAdd("Command finished.\r\n");
                SetBtnsEnDis(true);
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            bl.stop_work = true;
        }
    }
}
