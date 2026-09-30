# Installed plugins

Operator-installed DCMS plugins, loaded at startup by content-api and admin-api
(`Plugins:Directory`, mounted read-only at `/app/plugins`). One plugin per subfolder, holding its
`dotnet publish` output:

```
plugins/
  Acme.Dcms.Newsletter/
    Acme.Dcms.Newsletter.dll
    Acme.Dcms.Newsletter.deps.json
    (its private dependencies)
```

An installed plugin runs in-process with the host's rights: install only code you trust. A
plugin built against another SDK major, or one that fails to load, stops startup with a message
naming its folder. `Plugins:Disabled` switches any plugin off without removing it.

See docs/plugins.md, "Installing a plugin".
