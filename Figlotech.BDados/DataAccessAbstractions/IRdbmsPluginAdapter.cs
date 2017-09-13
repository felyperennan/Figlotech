﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace Figlotech.BDados.DataAccessAbstractions
{
    public interface IRdbmsPluginAdapter {
        IDbConnection GetNewConnection();
        IDbDataAdapter GetNewDataAdapter(IDbCommand command);
        IQueryGenerator QueryGenerator { get; }
        DataAccessorConfiguration Config { get; set; }
    }
}