using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace PicBoot
{
    public partial class frmSelectCPU : Form
    {
        const string cpu_params_file = "cpus.xml";
        List<CPU_Params> cpu_list = new List<CPU_Params>();

        public frmSelectCPU()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // ** most important: color theme :)
            this.BackColor = Color.Black;
            this.ForeColor = Color.White;
            //Now for every control that does need an extra color / property to be set use something like this
            foreach (Control ctrl in this.Controls)
            {
                ctrl.BackColor = this.BackColor;
                ctrl.ForeColor = this.ForeColor;
                //Maybe do more here...
            }
            this.Invalidate(); //Forces a re-draw of your controls / form

            // ** parse xml
            XmlDocument doc = new XmlDocument();
            try
            {
                doc.Load(cpu_params_file);
                cpu_list.Clear();
                foreach (XmlNode cpu_node in doc.SelectNodes("/cpu_list/cpu"))
                {
                    CPU_Params cp = new CPU_Params();
                    cp.name = cpu_node.SelectSingleNode("name").InnerText;

                    var b = cpu_node.SelectSingleNode("baud")?.InnerText;
                    if (b != null)
                        cp.baud = int.Parse(b);
                    if (cp.baud < 300 || cp.baud > 1000000)
                        cp.baud = 115200; // some default fallback

                    b = cpu_node.SelectSingleNode("timeout")?.InnerText;
                    if (b != null)
                        cp.timeout = int.Parse(b);
                    else
                        cp.timeout = 2000; // default 2 sec 

                    b = cpu_node.SelectSingleNode("prog_clear_pattern")?.InnerText;
                    if (b != null)
                        cp.prog_clear_pattern = uint.Parse(b, System.Globalization.NumberStyles.HexNumber);
                    else
                        cp.prog_clear_pattern = 0xFFFFFFFF; // default: clear memory FFFF....

                    cp.write_block = uint.Parse(cpu_node.SelectSingleNode("write_block").InnerText);
                    cp.read_block = uint.Parse(cpu_node.SelectSingleNode("read_block").InnerText);
                    cp.erase_block = uint.Parse(cpu_node.SelectSingleNode("erase_block").InnerText);
                    cp.max_pkt_size = uint.Parse(cpu_node.SelectSingleNode("max_pkt_size").InnerText);
                    cp.bytes_per_addr = uint.Parse(cpu_node.SelectSingleNode("bytes_per_addr").InnerText);
                    cp.prog_range = new List<AddrRange>();
                    foreach (XmlNode n in cpu_node.SelectNodes("prog"))
                    {
                        var pr = new AddrRange();
                        pr.first = uint.Parse(n.SelectSingleNode("first").InnerText, System.Globalization.NumberStyles.HexNumber);
                        pr.last = uint.Parse(n.SelectSingleNode("last").InnerText, System.Globalization.NumberStyles.HexNumber);
                        cp.prog_range.Add(pr);
                    }
                    var a = new AddrRange();
                    var data_rng_node = cpu_node.SelectSingleNode("data");
                    a.first = uint.Parse(data_rng_node.SelectSingleNode("first").InnerText, System.Globalization.NumberStyles.HexNumber);
                    a.last  = uint.Parse(data_rng_node.SelectSingleNode("last").InnerText, System.Globalization.NumberStyles.HexNumber);
                    cp.data_range = a;
                    cpu_list.Add(cp);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ERROR");
                this.Close();
                return;
            }

            // ** load combo box
            foreach (CPU_Params cpu in cpu_list)
            {
                cbSelectCPU.Items.Add(cpu.name);
            }
            cbSelectCPU.SelectedIndex = 0;
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void btnSelect_Click(object sender, EventArgs e)
        {
            int idx = cbSelectCPU.SelectedIndex;
            if (idx >= 0)
            {
                this.Hide();
                var f = new MainForm(cpu_list[idx]);
                f.ShowDialog();
                this.Show();
            }
        }
    }
}
