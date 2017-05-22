﻿using System;
using Figlotech.BDados.I18n;

namespace Figlotech.BDados {
    public class BDadosPortugueseStringsProvider : IBDadosStringsProvider {
        public string AUTH_USER_MAX_ATTEMPTS_EXCEEDED => "Tentativas de login excedidas, aguarde {0} minutos(s).";
        public string AUTH_USER_NOT_FOUND => "Usuário e/ou senha incorreto(s)";
        public string AUTH_PASSWORD_INCORRECT => "Usuário e/ou senha incorreto(s)";
        public string AUTH_USER_BLOCKED => "Usuário bloqueado";
        public string AUTH_PASSWORDS_MUST_MATCH => "Senha e confirmação precisam ser idênticas.";
        public string AUTH_USER_ALREADY_EXISTS => "Usuário já existe";
        public string BDIOC_CANNOT_RESOLVE_TYPE => "Não foi possível resolver o tipo '{0}'";
        public string SCOPY_ACCESSORS_CANNOT_BE_SAME => "SmartCopy não pode copiar de um repositório para ele mesmo.";
    }
}