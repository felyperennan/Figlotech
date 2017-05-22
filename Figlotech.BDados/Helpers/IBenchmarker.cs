﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Figlotech.BDados.Helpers
{
    public interface IBenchmarker {
        bool WriteToStdout { get; set; }

        double Mark(String txt);
        double TotalMark();
    }
}