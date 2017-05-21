﻿using Figlotech.BDados.Attributes;
using Figlotech.BDados.DataAccessAbstractions;
using Figlotech.BDados.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Figlotech.BDados.Authentication {
    public interface IUser : IDataObject {
        String Username { get; set; }
        String Password { get; set; }

        bool isActive { get; set; }

        IPermissionsContainer GetPermissionsContainer();
    }
}
