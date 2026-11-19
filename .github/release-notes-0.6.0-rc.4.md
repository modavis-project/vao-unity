# MODAVIS VAO Importer for Unity 0.6.0-rc.4

Unity 6 package for offline VAO import. The importer now reads the released
VAO 0.6.0 schemas and writes a 0.6.0 materialization receipt. It retains
the published 0.4.0 reader and the existing 0.5.0 snapshot reader.

MMIC 1.0.0 imports keep the carrier and manifest metadata. The receipt reports
the MMIC profile as incomplete. Use the VAO reference tools for domain
validation and execution.

In Unity Package Manager, choose **Add package from git URL** and enter
`https://github.com/modavis-project/vao-unity.git#v0.6.0-rc.4`. To install the attached package from disk,
check its SHA-256 first and use **Install package from tarball**.
