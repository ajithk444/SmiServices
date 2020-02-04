#!/bin/bash

# Install DATs dependencies to your local repo
mvn -ntp install:install-file -Dfile="./CTP.jar" -DgroupId="dat" -DartifactId="CTP" -Dversion="1.0" -Dpackaging="jar" -DgeneratePom="true"
mvn -ntp install:install-file -Dfile="./util.jar" -DgroupId="dat" -DartifactId="util" -Dversion="1.0" -Dpackaging="jar" -DgeneratePom="true"
mvn -ntp install:install-file -Dfile="./pixelmed_codec.jar" -DgroupId="dat" -DartifactId="pixelmed_codec" -Dversion="1.0" -Dpackaging="jar" -DgeneratePom="true"

# Install some required CTP dependencies
mvn -ntp install:install-file -Dfile="./clibwrapper_jiio.jar" -DgroupId="dat" -DartifactId="clibwrapper_jiio" -Dversion="1.1" -Dpackaging="jar" -DgeneratePom="true"
mvn -ntp install:install-file -Dfile="./jai_imageio.jar" -DgroupId="dat" -DartifactId="jai_imageio" -Dversion="1.1" -Dpackaging="jar" -DgeneratePom="true"

# Install DAT, giving it the pom we have generated
mvn -ntp install:install-file -Dfile="./DAT.jar" -DpomFile="./pom.xml"
