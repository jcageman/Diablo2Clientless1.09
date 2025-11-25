using System.Collections.Generic;
using System.IO;

namespace D2NG.Core.DataManager;

class PlainTextDataType
{
    private readonly List<string[]> m_lines;

    public PlainTextDataType(string file)
    {
        m_lines = [];
        var lines = new List<string>();

        using (StreamReader r = new(file))
        {
            string line;
            while ((line = r.ReadLine()) != null)
            {
                lines.Add(line);
            }
        }

        foreach (string line in lines)
        {
            string[] tokens = line.Split('|');
            m_lines.Add(tokens);
        }
    }

    public bool Get(int offset, out string output)
    {
        if (offset < 0 || offset >= m_lines.Count)
        {
            output = "";
            return false;
        }
        string[] line = m_lines[offset];
        output = line.Length == 0 ? "" : line[0];
        return true;
    }

    public bool Get(int offset, out string[] output)
    {
        if (offset < 0 || offset >= m_lines.Count)
        {
            output = null;
            return false;
        }
        output = m_lines[offset];
        return true;
    }
}